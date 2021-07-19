using Amazon.CDK;
using Amazon.CDK.AWS.EC2;

namespace Ec2Setup
{
    public class Ec2SetupStack : Stack
    {
        internal Ec2SetupStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            // The code that defines your stack goes here
            var vpc = new Vpc(this, "cdk_ec2_vpc", new VpcProps
            {
                Cidr = "10.255.252.0/23 ",
                NatGateways = 0, // Do not require NAT gateways
                SubnetConfiguration = new [] {
                    new SubnetConfiguration { CidrMask = 26, SubnetType = SubnetType.PUBLIC, Name = "cdk_ec2_elb_pub" },
                    new SubnetConfiguration { CidrMask = 26, SubnetType = SubnetType.ISOLATED, Name = "cdk_ec2_web_priv" },
                    new SubnetConfiguration { CidrMask = 26, SubnetType = SubnetType.ISOLATED, Name = "cdk_ec2_db_priv" }
                },
                MaxAzs = 3
            });
        }
    }
}
