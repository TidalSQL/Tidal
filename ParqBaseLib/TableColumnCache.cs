namespace ParqBaseLib
{
    using Parquet.Schema;
    using ParquetSharp.Schema;

    internal class TableColumnCache : ITableColumnCache
    {
        private Dictionary<string,Dictionary<string,string>> cache = new Dictionary<string, Dictionary<string, string>>();
        public void Add(string tableName, Dictionary<string, string> columns)
        {
            if (!cache.ContainsKey(tableName))
            {
                cache.Add(tableName, columns);
            }
        }
        public Dictionary<string,string> Get(string tableName)
        {
            if (cache.ContainsKey(tableName))
            {
                return cache[tableName];
            }

            throw new Exception("Table not found in cache");
        }
    }
}
