using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CdkEc2Setup
{
    sealed class Program
    {
        public static void Main(string[] args)
        {
            var app = new App();
            var setupStack = new Ec2SetupStack(app, "CdkEc2SetupStack", new StackProps
            {
                #region Account/region
                // If you don't specify 'env', this stack will be environment-agnostic.
                // Account/Region-dependent features and context lookups will not work,
                // but a single synthesized template can be deployed anywhere.

                // Uncomment the next block to specialize this stack for the AWS Account
                // and Region that are implied by the current CLI configuration.
                /*
                Env = new Amazon.CDK.Environment
                {
                    Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
                    Region = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_REGION"),
                }
                */

                // Uncomment the next block if you know exactly what Account and Region you
                // want to deploy the stack to.
                
                Env = new Amazon.CDK.Environment
                {
                    Account = "664224393056",
                    Region = "ap-southeast-1",
                }
                

                // For more information, see https://docs.aws.amazon.com/cdk/latest/guide/environments.html

                #endregion Account/region
            });
     
            app.Synth();
        }
    }
}
