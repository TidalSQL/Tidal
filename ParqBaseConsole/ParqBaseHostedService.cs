namespace ParqBaseConsole
{
    using Microsoft.Extensions.Hosting;
    using ParqBaseLib;
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    internal class ParqBaseHostedService : BackgroundService
    {
        private const int MaxLoginAttempts = 3;

        private readonly ParqBase db;
        private readonly IHostApplicationLifetime lifetime;

        public ParqBaseHostedService(ParqBase db, IHostApplicationLifetime lifetime)
        {
            this.db = db;
            this.lifetime = lifetime;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("Initializing ParqBase...");

            // Seed the default administrator on first run and surface its credentials once.
            var defaultPassword = Environment.GetEnvironmentVariable("PARQBASE_ADMIN_PASSWORD");
            if (string.IsNullOrEmpty(defaultPassword))
            {
                defaultPassword = "admin";
            }

            var init = this.db.InitializeServer(defaultPassword);
            if (init.Created)
            {
                Console.WriteLine();
                Console.WriteLine("First run: created default administrator login.");
                Console.WriteLine($"  Login:    {init.AdminLogin}");
                Console.WriteLine($"  Password: {init.Password}");
                Console.WriteLine("  Please change this password after signing in.");
            }

            return Task.Run(() =>
            {
                try
                {
                    if (!this.Authenticate(stoppingToken))
                    {
                        return;
                    }

                    Console.WriteLine();
                    Console.WriteLine($"ParqBase is running. Signed in as '{this.db.CurrentLogin}'.");
                    Console.WriteLine("Type SQL statements, 'exit' to quit.");

                    this.RunRepl(stoppingToken);
                }
                finally
                {
                    // Tell the generic host to shut down so the process actually exits
                    // instead of lingering until a Ctrl+C signal.
                    this.lifetime.StopApplication();
                }
            }, stoppingToken);
        }

        /// <summary>
        /// Gates the REPL behind a login. Users must authenticate as a valid server login (for
        /// example the default admin) before any SQL is accepted.
        /// </summary>
        private bool Authenticate(CancellationToken stoppingToken)
        {
            Console.WriteLine();
            Console.WriteLine("Please sign in to continue.");

            for (var attempt = 1; attempt <= MaxLoginAttempts; attempt++)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return false;
                }

                Console.Write("Login: ");
                var login = Console.ReadLine();
                if (login == null)
                {
                    return false; // End of input (e.g. Ctrl+Z / closed pipe).
                }

                var password = ReadPassword();
                if (password == null)
                {
                    return false;
                }

                if (this.db.Login(login.Trim(), password))
                {
                    return true;
                }

                var remaining = MaxLoginAttempts - attempt;
                Console.WriteLine(remaining > 0
                    ? $"Login failed. {remaining} attempt(s) remaining."
                    : "Login failed.");
            }

            Console.WriteLine("Too many failed attempts. Exiting.");
            return false;
        }

        private void RunRepl(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.Write("parqbase>> ");
                var input = Console.ReadLine();

                // null means end-of-input (e.g. Ctrl+Z / piped input closed).
                if (input == null || stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                input = input.Trim();
                if (input.Length == 0)
                {
                    continue;
                }

                if (string.Equals(input, "exit", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(input, "quit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (TryGetScriptPath(input, out var scriptPath))
                {
                    this.RunScriptFile(scriptPath);
                    continue;
                }

                var result = this.db.ExecuteQuery(input);

                if (result.Columns.Count > 0)
                {
                    Console.WriteLine(result.ToDisplayString());
                }
                else
                {
                    Console.WriteLine(result.Message);
                }
            }
        }

        /// <summary>
        /// Recognizes a "run a script file" command: either ':r &lt;path&gt;' (sqlcmd style) or
        /// 'run &lt;path&gt;'. Surrounding quotes on the path are stripped.
        /// </summary>
        private static bool TryGetScriptPath(string input, out string path)
        {
            path = string.Empty;
            string? remainder = null;

            if (input.StartsWith(":r ", StringComparison.OrdinalIgnoreCase))
            {
                remainder = input[3..];
            }
            else if (input.StartsWith("run ", StringComparison.OrdinalIgnoreCase))
            {
                remainder = input[4..];
            }

            if (remainder == null)
            {
                return false;
            }

            path = remainder.Trim().Trim('"');
            return path.Length > 0;
        }

        /// <summary>Runs every statement in a script file and prints a result per statement.</summary>
        private void RunScriptFile(string path)
        {
            IReadOnlyList<ParqBaseLib.QueryResult> results;
            try
            {
                results = this.db.ExecuteScriptFile(path, continueOnError: false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return;
            }

            var succeeded = 0;
            foreach (var result in results)
            {
                if (result.Columns.Count > 0)
                {
                    Console.WriteLine(result.ToDisplayString());
                }
                else if (!string.IsNullOrEmpty(result.Message))
                {
                    Console.WriteLine(result.Message);
                }

                if (result.Success)
                {
                    succeeded++;
                }
            }

            var failed = results.Count - succeeded;
            Console.WriteLine(failed == 0
                ? $"Script complete: {succeeded} statement(s) executed."
                : $"Script stopped after a failure: {succeeded} succeeded, {failed} failed.");
        }

        /// <summary>
        /// Reads a password. When the console is interactive the input is masked; when input is
        /// redirected (piped/scripted) it falls back to a plain read so automation still works.
        /// Returns null on end-of-input.
        /// </summary>
        private static string? ReadPassword()
        {
            Console.Write("Password: ");

            if (Console.IsInputRedirected)
            {
                return Console.ReadLine();
            }

            var password = string.Empty;
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return password;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0)
                    {
                        password = password[..^1];
                        Console.Write("\b \b");
                    }

                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    password += key.KeyChar;
                    Console.Write('*');
                }
            }
        }
    }
}
