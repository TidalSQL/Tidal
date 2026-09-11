namespace ParqBaseLib
{
    using System;
    using System.Globalization;
    using System.Linq;
    using Microsoft.SqlServer.TransactSql.ScriptDom;

    /// <summary>
    /// Scalar expression support: arithmetic, string concatenation, a library of common T-SQL
    /// scalar functions, CAST/CONVERT and CASE. Aggregate and window functions are handled by the
    /// query evaluator, not here.
    /// </summary>
    internal partial class ParqBaseStatementVisitor
    {
        private static readonly System.Collections.Generic.HashSet<string> AggregateFunctions =
            new(StringComparer.OrdinalIgnoreCase) { "COUNT", "SUM", "MIN", "MAX", "AVG", "COUNT_BIG" };

        private object? EvalBinary(BinaryExpression binary, Env env)
        {
            var left = this.GetScalarValue(binary.FirstExpression, env);
            var right = this.GetScalarValue(binary.SecondExpression, env);
            if (left == null || right == null)
            {
                return null;
            }

            // '+' doubles as string concatenation when either side is text.
            if (binary.BinaryExpressionType == BinaryExpressionType.Add &&
                (left is string || right is string))
            {
                return Stringify(left) + Stringify(right);
            }

            if (left is DateTime || right is DateTime)
            {
                throw new NotSupportedException("Arithmetic on datetime values is not supported; use DATEADD.");
            }

            var bothInt = left is int && right is int;
            if (bothInt)
            {
                var a = (int)left;
                var b = (int)right;
                return binary.BinaryExpressionType switch
                {
                    BinaryExpressionType.Add => (object)(a + b),
                    BinaryExpressionType.Subtract => a - b,
                    BinaryExpressionType.Multiply => a * b,
                    BinaryExpressionType.Divide => b == 0 ? throw new DivideByZeroException() : a / b,
                    BinaryExpressionType.Modulo => b == 0 ? throw new DivideByZeroException() : a % b,
                    _ => throw new NotSupportedException($"Unsupported operator: {binary.BinaryExpressionType}"),
                };
            }

            var da = ToDecimal(left);
            var db = ToDecimal(right);
            return binary.BinaryExpressionType switch
            {
                BinaryExpressionType.Add => (object)(da + db),
                BinaryExpressionType.Subtract => da - db,
                BinaryExpressionType.Multiply => da * db,
                BinaryExpressionType.Divide => db == 0 ? throw new DivideByZeroException() : da / db,
                BinaryExpressionType.Modulo => db == 0 ? throw new DivideByZeroException() : da % db,
                _ => throw new NotSupportedException($"Unsupported operator: {binary.BinaryExpressionType}"),
            };
        }

        private object? EvalFunction(FunctionCall call, Env env)
        {
            var name = call.FunctionName.Value.ToUpperInvariant();

            if (call.OverClause != null || string.Equals(name, "ROW_NUMBER", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    "Window functions are only supported directly in a SELECT list.");
            }

            if (AggregateFunctions.Contains(name))
            {
                throw new NotSupportedException(
                    $"Aggregate function {name} can only be used with GROUP BY or over the whole result.");
            }

            var args = call.Parameters;

            object? Arg(int i) => this.GetScalarValue(args[i], env);

            switch (name)
            {
                case "CONCAT":
                    return string.Concat(args.Select(a => Stringify(this.GetScalarValue(a, env))));

                case "CONCAT_WS":
                {
                    var sep = Stringify(Arg(0));
                    return string.Join(sep, args.Skip(1).Select(a => Stringify(this.GetScalarValue(a, env))));
                }

                case "LEN":
                case "DATALENGTH":
                {
                    var s = this.GetScalarValue(args[0], env);
                    return s == null ? (object?)null : Stringify(s).Length;
                }

                case "LEFT":
                {
                    var s = Stringify(Arg(0));
                    var n = Math.Max(0, ToInt(Arg(1)));
                    return n >= s.Length ? s : s.Substring(0, n);
                }

                case "RIGHT":
                {
                    var s = Stringify(Arg(0));
                    var n = Math.Max(0, ToInt(Arg(1)));
                    return n >= s.Length ? s : s.Substring(s.Length - n);
                }

                case "SUBSTRING":
                {
                    var s = Stringify(Arg(0));
                    var start = ToInt(Arg(1));
                    var length = ToInt(Arg(2));
                    var from = Math.Max(0, start - 1);
                    if (from >= s.Length || length <= 0)
                    {
                        return string.Empty;
                    }

                    return s.Substring(from, Math.Min(length, s.Length - from));
                }

                case "UPPER":
                    return Stringify(Arg(0)).ToUpperInvariant();

                case "LOWER":
                    return Stringify(Arg(0)).ToLowerInvariant();

                case "LTRIM":
                    return Stringify(Arg(0)).TrimStart();

                case "RTRIM":
                    return Stringify(Arg(0)).TrimEnd();

                case "TRIM":
                    return Stringify(Arg(0)).Trim();

                case "REPLACE":
                    return Stringify(Arg(0)).Replace(Stringify(Arg(1)), Stringify(Arg(2)));

                case "ABS":
                    return Math.Abs(ToDecimal(Arg(0)));

                case "ROUND":
                {
                    var value = ToDecimal(Arg(0));
                    var digits = args.Count > 1 ? ToInt(Arg(1)) : 0;
                    return Math.Round(value, Math.Max(0, digits), MidpointRounding.AwayFromZero);
                }

                case "CEILING":
                    return Math.Ceiling(ToDecimal(Arg(0)));

                case "FLOOR":
                    return Math.Floor(ToDecimal(Arg(0)));

                case "ISNULL":
                    return Arg(0) ?? Arg(1);

                case "COALESCE":
                    return args.Select(a => this.GetScalarValue(a, env)).FirstOrDefault(v => v != null);

                case "NULLIF":
                {
                    var a = Arg(0);
                    var b = Arg(1);
                    return (a != null && b != null && CompareTyped(a, b) == 0) ? null : a;
                }

                case "GETDATE":
                case "CURRENT_TIMESTAMP":
                    return DateTime.Now;

                case "GETUTCDATE":
                case "SYSUTCDATETIME":
                    return DateTime.UtcNow;

                case "SYSDATETIME":
                    return DateTime.Now;

                case "DATEADD":
                    return DateAdd(DatePartName(args[0]), ToInt(Arg(1)), ToDateTime(Arg(2)));

                case "DATEDIFF":
                    return DateDiff(DatePartName(args[0]), ToDateTime(Arg(1)), ToDateTime(Arg(2)));

                case "YEAR":
                    return ToDateTime(Arg(0)).Year;

                case "MONTH":
                    return ToDateTime(Arg(0)).Month;

                case "DAY":
                    return ToDateTime(Arg(0)).Day;

                case "CAST":
                case "CONVERT":
                    // These normally arrive as CastCall/ConvertCall nodes, but guard anyway.
                    throw new NotSupportedException("Use CAST(... AS type) / CONVERT(type, ...).");

                default:
                    throw new NotSupportedException($"Unsupported function: {name}.");
            }
        }

        private object? EvalSearchedCase(SearchedCaseExpression node, Env env)
        {
            foreach (var when in node.WhenClauses)
            {
                if (this.EvaluateBoolean(when.WhenExpression, env))
                {
                    return this.GetScalarValue(when.ThenExpression, env);
                }
            }

            return node.ElseExpression != null ? this.GetScalarValue(node.ElseExpression, env) : null;
        }

        private object? EvalSimpleCase(SimpleCaseExpression node, Env env)
        {
            var input = this.GetScalarValue(node.InputExpression, env);
            foreach (var when in node.WhenClauses)
            {
                var candidate = this.GetScalarValue(when.WhenExpression, env);
                if (input != null && candidate != null && CompareTyped(input, candidate) == 0)
                {
                    return this.GetScalarValue(when.ThenExpression, env);
                }
            }

            return node.ElseExpression != null ? this.GetScalarValue(node.ElseExpression, env) : null;
        }

        private static object? ConvertToType(object? value, DataTypeReference dataType)
        {
            if (value == null)
            {
                return null;
            }

            var sqlType = dataType.Name?.BaseIdentifier?.Value ?? "NVARCHAR";
            var physical = MapSqlType(sqlType);
            switch (physical)
            {
                case "string":
                    return Stringify(value);

                case "int":
                    return ToInt(value);

                case "decimal":
                {
                    var d = ToDecimal(value);
                    var scale = DecimalScale(dataType);
                    return scale.HasValue ? Math.Round(d, scale.Value, MidpointRounding.AwayFromZero) : d;
                }

                case "datetime":
                {
                    var dt = ToDateTime(value);
                    return string.Equals(sqlType, "DATE", StringComparison.OrdinalIgnoreCase) ? dt.Date : dt;
                }

                default:
                    return value;
            }
        }

        private static int? DecimalScale(DataTypeReference dataType)
        {
            if (dataType is SqlDataTypeReference sql && sql.Parameters.Count >= 2 &&
                sql.Parameters[1] is IntegerLiteral scale)
            {
                return int.Parse(scale.Value, CultureInfo.InvariantCulture);
            }

            return null;
        }

        private static string DatePartName(ScalarExpression expression) => expression switch
        {
            ColumnReferenceExpression c => c.MultiPartIdentifier.Identifiers.Last().Value.ToUpperInvariant(),
            StringLiteral s => s.Value.ToUpperInvariant(),
            _ => throw new NotSupportedException("Unsupported datepart."),
        };

        private static DateTime DateAdd(string part, int amount, DateTime date) => part switch
        {
            "YEAR" or "YY" or "YYYY" => date.AddYears(amount),
            "QUARTER" or "QQ" or "Q" => date.AddMonths(amount * 3),
            "MONTH" or "MM" or "M" => date.AddMonths(amount),
            "DAY" or "DD" or "D" or "DAYOFYEAR" or "DY" => date.AddDays(amount),
            "WEEK" or "WK" or "WW" => date.AddDays(amount * 7),
            "HOUR" or "HH" => date.AddHours(amount),
            "MINUTE" or "MI" or "N" => date.AddMinutes(amount),
            "SECOND" or "SS" or "S" => date.AddSeconds(amount),
            _ => throw new NotSupportedException($"Unsupported datepart: {part}"),
        };

        private static int DateDiff(string part, DateTime start, DateTime end) => part switch
        {
            "YEAR" or "YY" or "YYYY" => end.Year - start.Year,
            "MONTH" or "MM" or "M" => ((end.Year - start.Year) * 12) + end.Month - start.Month,
            "DAY" or "DD" or "D" => (int)(end.Date - start.Date).TotalDays,
            "HOUR" or "HH" => (int)(end - start).TotalHours,
            "MINUTE" or "MI" or "N" => (int)(end - start).TotalMinutes,
            "SECOND" or "SS" or "S" => (int)(end - start).TotalSeconds,
            _ => throw new NotSupportedException($"Unsupported datepart: {part}"),
        };

        private static string Stringify(object? value) => value switch
        {
            null => string.Empty,
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        private static int ToInt(object? value) => value switch
        {
            null => 0,
            int i => i,
            long l => (int)l,
            decimal d => (int)decimal.Truncate(d),
            double db => (int)db,
            string s => int.Parse(s, CultureInfo.InvariantCulture),
            _ => Convert.ToInt32(value, CultureInfo.InvariantCulture),
        };

        private static decimal ToDecimal(object? value) => value switch
        {
            null => 0m,
            decimal d => d,
            int i => i,
            long l => l,
            double db => (decimal)db,
            string s => decimal.Parse(s, CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        };

        private static DateTime ToDateTime(object? value) => value switch
        {
            null => default,
            DateTime dt => dt,
            string s => DateTime.Parse(s, CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException($"Cannot convert {value.GetType().Name} to datetime."),
        };
    }
}
