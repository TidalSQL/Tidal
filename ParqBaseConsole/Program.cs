namespace ParqBaseConsole
{
    using ParqBaseLib;
    using System.Reflection;

    internal class Program
    {
        private ParqBase db = new ParqBase();
        static void Main(string[] args)
        {
            Console.WriteLine("Initializing ParqBase...");
            Console.WriteLine("ParqBase is running");
            Console.WriteLine("Setting up database location");
            Console.WriteLine($"Database path [{Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)}]");

            var p = new Program();
            p.CreateDatabase("create database doreameyTest");
            p.CreateDatabase("create database andrea");
            p.CreateDatabase("use doreameyTest");

            p.ExecuteStatement("CREATE TABLE Salaries (EmployeeID INT PRIMARY KEY, Salary MONEY);");
            p.ExecuteStatement("CREATE TABLE Employees (EmployeeID INT PRIMARY KEY, FirstName NVARCHAR(50), LastName NVARCHAR(50), HireDate DATE);");
            p.ExecuteStatement("CREATE TABLE Departments (DepartmentID INT PRIMARY KEY, DepartmentName NVARCHAR(100));");
            p.ExecuteStatement("CREATE TABLE Projects (ProjectID INT PRIMARY KEY, ProjectName NVARCHAR(100), StartDate DATE, EndDate DATE);");
            p.ExecuteStatement("CREATE TABLE EmployeeProjects (EmployeeID INT, ProjectID INT, PRIMARY KEY (EmployeeID, ProjectID), FOREIGN KEY (EmployeeID) REFERENCES Employees(EmployeeID), FOREIGN KEY (ProjectID) REFERENCES Projects(ProjectID));");

            p.ExecuteStatement(
                "INSERT INTO Employees (EmployeeID, FirstName, LastName, HireDate)" +
                "VALUES (1, 'John', 'Doe', '2024-02-17');");


            Console.WriteLine("parqbase>>");
            Console.ReadLine();
        }

        public void ExecuteStatement(string statement)
        {
            this.db.ExecuteStatement(statement);
        }

        public void CreateDatabase(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new Exception("invalid database name");
            }

            this.db.ExecuteStatement(name);
        }

        public void UseDatabase(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new Exception("invalid database name");
            }

            this.db.ExecuteStatement(name);
        }
    }
}
