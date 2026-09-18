namespace ParqBaseLib
{
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Optimizes the comma-separated FROM list of a SELECT (implicit inner joins) so that TPC-H-style
    /// analytic queries do not build a full cartesian product. Single-table WHERE predicates are pushed
    /// down onto each input, and the inputs are combined with hash equi-joins derived from the WHERE
    /// clause's equality terms. The caller still applies the complete WHERE clause afterward, so this is
    /// purely a performance optimization: only rows that satisfy a subset of the predicate are kept, and
    /// no row required by the original semantics is ever dropped.
    /// </summary>
    internal partial class ParqBaseStatementVisitor
    {
        private readonly record struct JoinEdge(int BaseA, ScalarExpression ExprA, int BaseB, ScalarExpression ExprB);

        private RowSet BuildImplicitJoin(List<RowSet> bases, BooleanExpression? where, Env? outer)
        {
            if (bases.Count == 1)
            {
                return bases[0];
            }

            // Map qualifiers and column names to the base they come from, for classifying predicates.
            var qualifierToBase = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var nameToBases = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < bases.Count; i++)
            {
                foreach (var col in bases[i].Schema)
                {
                    if (!string.IsNullOrEmpty(col.Qualifier))
                    {
                        qualifierToBase[col.Qualifier] = i;
                    }

                    if (!nameToBases.TryGetValue(col.Name, out var list))
                    {
                        list = new List<int>();
                        nameToBases[col.Name] = list;
                    }

                    if (!list.Contains(i))
                    {
                        list.Add(i);
                    }
                }
            }

            var conjuncts = where != null ? SplitConjunction(where).ToList() : new List<BooleanExpression>();

            var localPredicates = new Dictionary<int, List<BooleanExpression>>();
            var edges = new List<JoinEdge>();
            foreach (var conj in conjuncts)
            {
                var refs = this.ResolveBases(conj, qualifierToBase, nameToBases);
                if (refs != null && refs.Count == 1)
                {
                    var only = refs.First();
                    if (!localPredicates.TryGetValue(only, out var preds))
                    {
                        preds = new List<BooleanExpression>();
                        localPredicates[only] = preds;
                    }

                    preds.Add(conj);
                    continue;
                }

                if (conj is BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } cmp)
                {
                    var lb = this.ResolveBases(cmp.FirstExpression, qualifierToBase, nameToBases);
                    var rb = this.ResolveBases(cmp.SecondExpression, qualifierToBase, nameToBases);
                    if (lb != null && rb != null && lb.Count == 1 && rb.Count == 1 && lb.First() != rb.First())
                    {
                        edges.Add(new JoinEdge(lb.First(), cmp.FirstExpression, rb.First(), cmp.SecondExpression));
                    }
                }
            }

            // Push single-table predicates down to shrink each input before joining.
            var filtered = new RowSet[bases.Count];
            for (var i = 0; i < bases.Count; i++)
            {
                filtered[i] = bases[i];
                if (localPredicates.TryGetValue(i, out var preds))
                {
                    foreach (var pred in preds)
                    {
                        var kept = new List<object?[]>();
                        foreach (var row in filtered[i].Rows)
                        {
                            if (this.EvaluateBoolean(pred, new Env(filtered[i].Schema, row, outer)))
                            {
                                kept.Add(row);
                            }
                        }

                        filtered[i] = new RowSet(filtered[i].Schema, kept);
                    }
                }
            }

            // Greedy join: seed with the smallest input, then repeatedly attach the smallest remaining
            // input reachable by an equi-join edge (cross-joining only when nothing connects).
            var remaining = Enumerable.Range(0, bases.Count).ToList();
            remaining.Sort((a, b) => filtered[a].Rows.Count.CompareTo(filtered[b].Rows.Count));
            var seed = remaining[0];
            remaining.RemoveAt(0);
            var joined = filtered[seed];
            var joinedBases = new HashSet<int> { seed };

            while (remaining.Count > 0)
            {
                var pick = -1;
                List<JoinEdge>? useEdges = null;
                foreach (var t in remaining)
                {
                    var connecting = edges.Where(e =>
                        (joinedBases.Contains(e.BaseA) && e.BaseB == t) ||
                        (joinedBases.Contains(e.BaseB) && e.BaseA == t)).ToList();
                    if (connecting.Count > 0 && (pick == -1 || filtered[t].Rows.Count < filtered[pick].Rows.Count))
                    {
                        pick = t;
                        useEdges = connecting;
                    }
                }

                if (pick == -1)
                {
                    // No equi-join connects any remaining input: fall back to a cross join.
                    pick = remaining[0];
                    joined = this.LimitedCrossJoin(joined, filtered[pick], null);
                }
                else
                {
                    var leftKeys = new List<ScalarExpression>();
                    var rightKeys = new List<ScalarExpression>();
                    foreach (var e in useEdges!)
                    {
                        if (e.BaseA == pick)
                        {
                            leftKeys.Add(e.ExprB);
                            rightKeys.Add(e.ExprA);
                        }
                        else
                        {
                            leftKeys.Add(e.ExprA);
                            rightKeys.Add(e.ExprB);
                        }
                    }

                    joined = this.HashEquiJoin(joined, filtered[pick], leftKeys, rightKeys, outer);
                }

                joinedBases.Add(pick);
                remaining.Remove(pick);
            }

            return joined;
        }

        // Inner equi-join of two materialized row sets. The hash table is built on the smaller input.
        private RowSet HashEquiJoin(
            RowSet left, RowSet right, List<ScalarExpression> leftKeys, List<ScalarExpression> rightKeys, Env? outer)
        {
            var schema = left.Schema.Concat(right.Schema).ToList();
            var rows = new List<object?[]>();
            var buildOnRight = right.Rows.Count <= left.Rows.Count;
            var buildRows = buildOnRight ? right.Rows : left.Rows;
            var buildKeys = buildOnRight ? rightKeys : leftKeys;
            var buildSchema = buildOnRight ? right.Schema : left.Schema;
            var probeRows = buildOnRight ? left.Rows : right.Rows;
            var probeKeys = buildOnRight ? leftKeys : rightKeys;
            var probeSchema = buildOnRight ? left.Schema : right.Schema;

            var lookup = new Dictionary<string, List<object?[]>>();
            foreach (var row in buildRows)
            {
                var key = this.JoinKey(buildKeys, buildSchema, row, outer);
                if (key == null)
                {
                    continue;
                }

                if (!lookup.TryGetValue(key, out var bucket))
                {
                    bucket = new List<object?[]>();
                    lookup[key] = bucket;
                }

                bucket.Add(row);
            }

            var counter = 0;
            foreach (var row in probeRows)
            {
                if ((counter++ & 0x3FFF) == 0)
                {
                    this.ThrowIfCancelled();
                }

                var key = this.JoinKey(probeKeys, probeSchema, row, outer);
                if (key == null || !lookup.TryGetValue(key, out var bucket))
                {
                    continue;
                }

                foreach (var matched in bucket)
                {
                    rows.Add(buildOnRight ? Concat(row, matched) : Concat(matched, row));
                }
            }

            return new RowSet(schema, rows);
        }

        // Builds a composite key string for the given key expressions, or null if any component is NULL
        // (SQL equi-joins never match on NULL).
        private string? JoinKey(List<ScalarExpression> keys, List<ColumnRef> schema, object?[] row, Env? outer)
        {
            var env = new Env(schema, row, outer);
            var parts = new string[keys.Count];
            for (var i = 0; i < keys.Count; i++)
            {
                var value = this.GetScalarValue(keys[i], env);
                if (value == null)
                {
                    return null;
                }

                parts[i] = Stringify(value);
            }

            return string.Join("\u0001", parts);
        }

        // Returns the set of base indices a fragment references, or null if any column is unqualified and
        // ambiguous (present in more than one base) or otherwise unresolvable — in which case the caller
        // conservatively treats the predicate as spanning everything (left for the final WHERE pass).
        private HashSet<int>? ResolveBases(
            TSqlFragment fragment, Dictionary<string, int> qualifierToBase, Dictionary<string, List<int>> nameToBases)
        {
            var collector = new ColumnCollector();
            fragment.Accept(collector);
            var set = new HashSet<int>();
            foreach (var col in collector.Columns)
            {
                var b = ResolveColumnBase(col, qualifierToBase, nameToBases);
                if (b == null)
                {
                    return null;
                }

                set.Add(b.Value);
            }

            return set;
        }

        private static int? ResolveColumnBase(
            ColumnReferenceExpression col, Dictionary<string, int> qualifierToBase, Dictionary<string, List<int>> nameToBases)
        {
            var ids = col.MultiPartIdentifier.Identifiers;
            var name = ids.Last().Value;
            if (ids.Count > 1)
            {
                var qualifier = ids[ids.Count - 2].Value;
                return qualifierToBase.TryGetValue(qualifier, out var qb) ? qb : (int?)null;
            }

            return nameToBases.TryGetValue(name, out var list) && list.Count == 1 ? list[0] : (int?)null;
        }

        private sealed class ColumnCollector : TSqlFragmentVisitor
        {
            public List<ColumnReferenceExpression> Columns { get; } = new();

            public override void Visit(ColumnReferenceExpression node) => this.Columns.Add(node);
        }
    }
}
