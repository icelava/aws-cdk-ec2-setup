using Amazon.CDK;
using Amazon.CDK.AWS.EC2;

namespace Ec2Setup
{
    public class Ec2SetupStack : Stack
    {
        private Vpc vpc;
        private string vpcCidr = "10.255.248.0/21";
        private string internetCidr = "0.0.0.0/0";

        internal Ec2SetupStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            this.EstablishNetwork();
        }

        private void EstablishNetwork()
        {
            this.vpc = new Vpc(this, "cdk_ec2_vpc",
            new VpcProps
            {
                Cidr = vpcCidr,
                MaxAzs = 2,
                NatGateways = 0, // Do not require NAT gateways.

                SubnetConfiguration = new [] {
                    new SubnetConfiguration { CidrMask = 28, SubnetType = SubnetType.PUBLIC, Name = "cdk_ec2_elb_pub" },
                    new SubnetConfiguration { CidrMask = 26, SubnetType = SubnetType.ISOLATED, Name = "cdk_ec2_web_priv" }
                }
            });

            this.CustomiseRouteTable();
        }

        private void CustomiseRouteTable()
        {
            // Custom route table and routes.
            var customRtName = "CustomRouteTable";
            var elbRouteTable = new CfnRouteTable(vpc, customRtName,
            new CfnRouteTableProps
            {
                VpcId = this.vpc.VpcId,
            });
            elbRouteTable.Node.AddDependency(vpc.PublicSubnets);
            elbRouteTable.Node.AddDependency(vpc.IsolatedSubnets);

            // Looks like the ultimate name given to the custom RouteTable won't have "CustomRouteTable" in the output template;
            // only goes as far as its parent scope "Ec2SetupStack/cdk_ec2_vpc"; it has to be manually revised.
            var revisedName = vpc.Stack.StackName + "/" + vpc.Node.Id + "/" + customRtName;
            Amazon.CDK.Tags.Of(elbRouteTable).Add("Name", revisedName);
            //elbRouteTable.Tags.SetTag("Name", customRtName);

            var internetRoute = new CfnRoute(elbRouteTable, "InternetRoute",
            new CfnRouteProps
            {
                RouteTableId = elbRouteTable.Ref,
                DestinationCidrBlock = internetCidr,
                GatewayId = this.vpc.InternetGatewayId
            });
            internetRoute.Node.AddDependency(elbRouteTable);

            this.ReAssociateRouteTable(this.vpc, this.vpc.PublicSubnets, elbRouteTable);
            this.ReAssociateRouteTable(this.vpc, this.vpc.IsolatedSubnets, elbRouteTable);
        }

        private void ReAssociateRouteTable(Construct scope, ISubnet[] subnets, CfnRouteTable routeTable)
        {
            foreach (var subnet in subnets)
            {
                var routeTableAssoc = new CfnSubnetRouteTableAssociation(scope, subnet.Node.Id + "_" + routeTable.Node.Id,
                new CfnSubnetRouteTableAssociationProps
                {
                    SubnetId = subnet.SubnetId,
                    RouteTableId = routeTable.Ref
                });
            }
        }

                private void ConfigurePublic()
        {

        }

        private void ConfigurePrivate()
        {

        }

        private void EstablishEC2()
        {

        }
    }
}
