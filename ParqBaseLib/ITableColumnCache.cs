namespace ParqBaseLib
{
    internal interface ITableColumnCache
    {
        public void Add(string tableName, Dictionary<string, string> columns);
        public Dictionary<string, string> Get(string tableName);
    }
}