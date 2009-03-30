using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Web.Deployment;

namespace MySqlCustomProvider
{
    [DeploymentProviderFactory]
    public class MySqlProviderFactory : DeploymentProviderFactory
    {
        protected override DeploymentObjectProvider Create(DeploymentProviderContext providerContext, DeploymentBaseContext baseContext)
        {
            return new MySqlProvider(providerContext, baseContext);
        }

        public override string Description
        {
            get { return @"Sample custom provider to sync MySql databases"; }
        }

        public override string ExamplePath
        {
            get { return @"Server=localhost;Database=db1;uid=root;pwd=iis6!dfu"; }
        }

        public override DeploymentProviderSettingInfo[] GetSupportedSettings()
        {
            return new DeploymentProviderSettingInfo[] 
            {
                new MySqlDumpExecutablePath()
            };
        }

        public override string FriendlyName
        {
            get { return "dbFullMySql"; }
        }

        public override string Name
        {
            get { return "dbFullMySql"; }
        }
    }
}
