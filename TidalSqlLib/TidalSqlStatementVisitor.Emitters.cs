using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TidalSqlLib
{
    internal partial class TidalSqlStatementVisitor
    {
        /// <summary>
        /// Emits a multi-part identifier values as string, e.g., schema object. Required.
        /// Used when determining whether two objects are the same object.
        /// </summary>
        private string EmitMultiPartNameValue(SchemaObjectName node)
        {
            return string.Join(".", node.Identifiers.Select(f => f.Value));
        }

        /// <summary>
        /// Emits a multi-part identifier values with Quote as string.
        /// Used when displaying the names in messages.
        /// </summary>
        private string EmitMultiPartNameWithQuote(SchemaObjectName node)
        {
            return string.Join(".", node.Identifiers.Select(f => this.QuoteIdentifier(f)));
        }

        private string EmitMultiPartNameWithQuote(MultiPartIdentifier node)
        {
            return string.Join(".", node.Identifiers.Select(i => this.QuoteIdentifier(i)));
        }

        private string QuoteIdentifier(Identifier identifier)
        {
            if (identifier == null)
            {
                return string.Empty;
            }

            if (identifier.QuoteType == QuoteType.NotQuoted)
            {
                return identifier.Value;
            }

            if (identifier.QuoteType == QuoteType.SquareBracket)
            {
                return $"{"["}{identifier.Value}{"]"}";
            }

            if (identifier.QuoteType == QuoteType.DoubleQuote)
            {
                return $"{"\""}{identifier.Value}{"\""}";
            }

            throw new Exception("Unknown quote type found during translation");
        }
    }
}
