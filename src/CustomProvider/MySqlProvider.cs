using System;
using System.IO;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using Microsoft.Win32;
using Microsoft.Web.Deployment;

namespace MySqlCustomProvider
{
    public class MySqlProvider : DeploymentObjectProvider
    {
        internal const string ObjectName = "dbFullMySql";
        internal const string KeyAttributeName = "path";

        #region Custom Provider Override Methods

        public MySqlProvider(DeploymentProviderContext providerContext,
            DeploymentBaseContext baseContext)
            : base(providerContext, baseContext)
        {
        }

        public override string Name
        {
            get
            {
                return MySqlProvider.ObjectName;
            }
        }

        public override void GetAttributes(DeploymentAddAttributeContext addContext)
        {
            // The source path should only be an absolute physical path or a valid connection string
            if (IsAbsolutePhysicalPath(this.Path))
            {
                if (!File.Exists(this.Path))
                {
                    throw new DeploymentFatalException("File " + this.Path + " is not accessible");
                }
            }
            else
            {
                if (!EnsureMySqlConnection(this.Path))
                {
                    throw new DeploymentFatalException("Database " + this.Path + " is not accessible");
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Delete the temporary intermediate file
                if (File.Exists(MySqlTempFilePath))
                {
                    File.Delete(MySqlTempFilePath);
                }
            }
        }

        public override DeploymentObjectAttributeData CreateKeyAttributeData()
        {
            return new DeploymentObjectAttributeData(MySqlProvider.KeyAttributeName,
                this.Path,
                DeploymentObjectAttributeKind.CaseInsensitiveCompare);
        }

        // This method is called by when syncing to an archive.
        public override Stream GetStream()
        {
            FileStream _reader = null;

            if (IsAbsolutePhysicalPath(this.Path))
            {
                _reader = new FileStream(this.Path, FileMode.Open, FileAccess.Read);
            }
            else
            {
                ExecuteMySqlDumpOnConnectionString(this.Path);
                _reader = new FileStream(MySqlTempFilePath, FileMode.Open, FileAccess.Read);
            }

            return _reader;
        }

        public override DeploymentObjectResolver GetObjectResolver()
        {
            return new DirPathObjectResolver();
        }

        // Add is called by the destination object when performing a sync
        public override void Add(DeploymentObject source, bool whatIf)
        {
            if (whatIf)
            {
                return;
            }            

            string MySqlTempFilePath = StreamToFile(source);

            // This handles syncing a script to a database
            if (IsAbsolutePhysicalPath(source.ProviderContext.Path))
            {
                SyncScriptToMySqlDatabase(source.ProviderContext.Path, this.Path);
            }
            else
            {
                ExecuteMySqlDumpOnConnectionString(source.ProviderContext.Path);
                string _output = File.ReadAllText(MySqlTempFilePath);

                // This handles syncing a database to a script
                if (IsAbsolutePhysicalPath(this.Path))
                {
                    File.AppendAllText(this.Path, _output);
                }
                else
                {
                    // This handles syncing a database to a database
                    SyncScriptToMySqlDatabase(MySqlTempFilePath, this.Path);
                }
            }
        }

        private string StreamToFile(DeploymentObject source)
        {
            // create a temp file with binary writer handle as destination
            string tempDirectory = System.IO.Path.GetTempPath();
            System.IO.Directory.CreateDirectory(tempDirectory);

            string tempFileName = tempDirectory + "\\temp.sql";

            FileStream mysqlStreamDest = new FileStream(tempFileName, FileMode.Create, FileAccess.ReadWrite);

            // Get the stream from the zip file
            System.IO.Stream mysqlStreamSource = source.GetStream();

            const int bufferSize = 32768;
            byte[] buffer = new byte[bufferSize];
            int actualBytesRead;

            //re-serialize the source MSDeploy file into a temporary file
            while ((actualBytesRead = mysqlStreamSource.Read(buffer, 0, bufferSize)) > 0)
            {
                try
                {
                    mysqlStreamDest.Write(buffer, 0, actualBytesRead);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
            mysqlStreamDest.Flush();
            mysqlStreamDest.Close();

            return (tempFileName);
        }

        // This method uses msdeploy APIs to sync a script to a database using dbMySql provider
        public void SyncScriptToMySqlDatabase(string sourceMySqlScriptFilePath, string destinationMySqlConnectionStringPath)
        {
            DeploymentProviderOptions sourceProviderOptions = new DeploymentProviderOptions(DeploymentWellKnownProvider.DBMySql);
            sourceProviderOptions.Path = sourceMySqlScriptFilePath;

            using (DeploymentObject sourceObject = DeploymentManager.CreateObject(sourceProviderOptions, new DeploymentBaseOptions()))
            {
                DeploymentProviderOptions destProviderOptions = new DeploymentProviderOptions(DeploymentWellKnownProvider.DBMySql);
                destProviderOptions.Path = destinationMySqlConnectionStringPath;

                sourceObject.SyncTo(destProviderOptions, new DeploymentBaseOptions(), new DeploymentSyncOptions());
            }
        }

        public override void Update(DeploymentObject source, bool whatIf)
        {
            this.Add(source, whatIf);
        }

        // This ensures that GetStream() is called
        public override DeploymentObjectKind ObjectKind
        {
            get
            {
                return DeploymentObjectKind.ContainsStream;
            }
        }

        #endregion

        #region Helper Methods
        public static bool IsAbsolutePhysicalPath(string path)
        {
            if (string.IsNullOrEmpty(path) || (path.Length < 3))
            {
                //too short
                return false;
            }
            else if ((path[1] == System.IO.Path.VolumeSeparatorChar) && IsDirectorySeparatorChar(path[2]))
            {
                //its like c:\
                return true;
            }
            else if (IsDirectorySeparatorChar(path[0]) && IsDirectorySeparatorChar(path[1]))
            {
                // its a share 
                return true;
            }
            else
            {
                //its unknown
                return false;
            }
        }

        private static bool IsDirectorySeparatorChar(char c)
        {
            if (c == System.IO.Path.DirectorySeparatorChar ||
                c == System.IO.Path.AltDirectorySeparatorChar)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        // This method runs mysqldump.exe on the database
        private void ExecuteMySqlDumpOnConnectionString(string connectionString)
        {
            Dictionary<string, string> sourceConnectionStringItems = new Dictionary<string, string>();
            Process mysqldump = new Process();

            DbConnectionStringBuilder sourceConnectionStringBuilder = new DbConnectionStringBuilder();
            sourceConnectionStringBuilder.ConnectionString = connectionString;

            sourceConnectionStringItems = AddConnectionStringItemsToDictionary(sourceConnectionStringBuilder.ConnectionString);

            mysqldump.StartInfo.WorkingDirectory = Environment.CurrentDirectory;
            mysqldump.StartInfo.FileName = MySqlDumpExeFilePath;
            mysqldump.StartInfo.Arguments = ReplaceConnectionStringItemsAsMySqlDumpArguments(sourceConnectionStringItems);
            mysqldump.StartInfo.CreateNoWindow = false;
            mysqldump.StartInfo.RedirectStandardOutput = true;
            mysqldump.StartInfo.UseShellExecute = false;
            mysqldump.Start();

            StreamReader _stdOutput = mysqldump.StandardOutput;
            string _streamOutput = _stdOutput.ReadToEnd();

            // Append output to a temporary file
            File.AppendAllText(MySqlTempFilePath, _streamOutput);

            _stdOutput.Close();
        }

        private DbConnection GetConnection(string connectionString)
        {
            DbConnection connection = DbProviderFactories.GetFactory("MySql.Data.MySqlClient").CreateConnection();
            connection.ConnectionString = connectionString;
            return connection;
        }

        private bool EnsureMySqlConnection(string connectionString)
        {
            try
            {
                DbConnection connection = GetConnection(connectionString);
                connection.Open();
                connection.Close();

                return true;
            }
            catch (Exception)
            {
                throw new DeploymentFatalException("Could not access " + connectionString);
            }
        }

        // Convert connection string to name-value pairs
        private Dictionary<string, string> AddConnectionStringItemsToDictionary(string connectionString)
        {
            Dictionary<string, string> connectionStringItems = new Dictionary<string, string>();

            string[] splitStrings = connectionString.Split(new char[] { ';' });
            string[] nameValuePairs = new string[2];

            foreach (string connectionStringEntity in splitStrings)
            {
                nameValuePairs = connectionStringEntity.Split(new char[] { '=' });

                // Convert to standard format
                if (nameValuePairs[0].Equals("user id", StringComparison.OrdinalIgnoreCase))
                {
                    nameValuePairs[0] = "uid";
                }

                if (nameValuePairs[0].Equals("password", StringComparison.OrdinalIgnoreCase))
                {
                    nameValuePairs[0] = "pwd";
                }

                connectionStringItems.Add(nameValuePairs[0], nameValuePairs[1]);
            }

            return connectionStringItems;
        }

        // Construct arguments to mysqldump.exe based of connection string items
        private string ReplaceConnectionStringItemsAsMySqlDumpArguments(Dictionary<string, string> connectionStringItems)
        {
            return (@"--host=" + connectionStringItems["server"]
                               + @" " + connectionStringItems["database"]
                               + @" --user=" + connectionStringItems["uid"]
                               + @" --password=" + connectionStringItems["pwd"]
                               + @" --no-create-db");
        }

        #endregion

        #region Variables
        private string Path
        {
            get
            {
                return this.ProviderContext.Path;
            }
        }

        // Temporary file to hold output of mysqldump.exe
        private string MySqlTempFilePath
        {
            get
            {
                _mysqlTempFilePath = Convert.ToString(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\IIS Extensions\MSDeploy\1",
                                                       "InstallPath", Environment.CurrentDirectory) + @"MySqlTempFile.sql");

                return _mysqlTempFilePath;
            }
        }

        // Location of mysqlump.exe
        private string MySqlDumpExeFilePath
        {
            get
            {
                MySqlDumpExecutablePath exePath = new MySqlDumpExecutablePath();
                _mysqlDumpExePath = this.ProviderContext.ProviderSettings.GetValueOrDefault(
                                                                                 exePath.Name,
                 Environment.ExpandEnvironmentVariables("%programfiles%") + @"\MySQL\MySQL Server 5.1\bin\mysqldump.exe");

                return _mysqlDumpExePath;
            }
        }

        private string _mysqlTempFilePath;
        private string _mysqlDumpExePath;
        #endregion
    }

    internal class DirPathObjectResolver : DeploymentObjectResolver
    {
        public override string GetAbsolutePath(DeploymentObject target)
        {
            Debug.Assert(target.KeyAttribute != null);
            if (target.Parent != null && target.Parent.Name == "dirPath")
            {
                return Path.Combine(target.Parent.AbsolutePath, target.KeyAttribute.GetValue<string>());
            }
            else
            {
                return target.KeyAttribute.GetValue<string>();
            }
        }

        public override string GetSummary(DeploymentObject deploymentObject)
        {
            return deploymentObject.KeyAttribute.GetValue<string>();
        }
    }

    public class MySqlDumpExecutablePath : DeploymentProviderSettingInfo
    {
        public MySqlDumpExecutablePath()
        {
        }

        public override string Name
        {
            get
            {
                return "mysqlDumpExecutablePath";
            }
        }

        public override string Description
        {
            get
            {
                return "Full physical path to mysqldump.exe";
            }
        }

        public override Type Type
        {
            get
            {
                return typeof(string);
            }
        }

        public override object Validate(object value)
        {
            string mysqlDumpExePath = value as string;

            if (!MySqlProvider.IsAbsolutePhysicalPath(mysqlDumpExePath))
            {
                throw new DeploymentFatalException(mysqlDumpExePath + " is not a valid absolute physical path");
            }

            if (!File.Exists(mysqlDumpExePath))
            {
                throw new DeploymentFatalException(mysqlDumpExePath + " does not exist");
            }

            return mysqlDumpExePath;
        }

        public override string FriendlyName
        {
            get
            {
                return "mysqlDumpExecutablePath";
            }
        }
    }
}