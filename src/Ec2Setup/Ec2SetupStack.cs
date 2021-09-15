using Amazon.CDK;
using Amazon.CDK.AWS.AutoScaling;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using System;
using System.IO;



namespace Ec2Setup
{
	public class Ec2SetupStack : Stack
	{
		private Vpc vpc;
		private string vpcCidr = "10.255.248.0/21";
		private string internetCidr = "0.0.0.0/0";
		private double maxAvailabilityZones = 3;

		private string publicElbSubnetName = "cdk_ec2_elb_pub";
		private string privateWebSubnetName = "cdk_ec2_web_priv";

		private SecurityGroup pubElbSG;
		private string pubElbSGName = "cdk_ec2_elb_pub_sg";
		private SecurityGroup prviWebSG;
		private string prviWebSGName = "cdk_ec2_web_priv_sg";

		private string sshKey = "ec2_exps";

		private AutoScalingGroup webServersAsg;

		internal Ec2SetupStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
		{
			this.EstablishNetwork();
			this.EstablishEC2();
		}

		private void EstablishNetwork()
		{
			this.vpc = new Vpc(this, "cdk_ec2_vpc",
			new VpcProps
			{
				Cidr = vpcCidr,
				MaxAzs = maxAvailabilityZones,
				NatGateways = 0, // Do not require NAT gateways.

				SubnetConfiguration = new[] {
						  new SubnetConfiguration { CidrMask = 28, SubnetType = SubnetType.PUBLIC, Name =  this.publicElbSubnetName},
						  new SubnetConfiguration { CidrMask = 26, SubnetType = SubnetType.PUBLIC, Name =  this.privateWebSubnetName}
				 }
			});

			this.CustomiseRouteTable();
			this.TightenNacls();
			this.TightenSecurityGroups();
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

		private void TightenNacls()
		{
			var elbSubnets = this.vpc.SelectSubnets(new SubnetSelection { SubnetGroupName = this.publicElbSubnetName });
			var webSubnets = this.vpc.SelectSubnets(new SubnetSelection { SubnetGroupName = this.privateWebSubnetName });

			var ephemeralPortRange = AclTraffic.TcpPortRange(1024, 65535);

			#region Public web ELB

			var elbNaclName = "cdk_ec2_elb_pub_nacl";
			var elbNacl = new NetworkAcl(this.vpc, elbNaclName,
			new NetworkAclProps
			{
				NetworkAclName = elbNaclName,
				Vpc = this.vpc,
				SubnetSelection = new SubnetSelection { SubnetGroupName = this.publicElbSubnetName }
			});


			// Allow return HTTP response from private web subnets.
			var pubElbPrivWebResponseRule = 50;
			foreach (var subnet in webSubnets.Subnets)
			{
				elbNacl.AddEntry("Incoming_HTTP_response_" + subnet.Node.Id, new CommonNetworkAclEntryOptions
				{
					RuleNumber = pubElbPrivWebResponseRule,
					Direction = TrafficDirection.INGRESS,
					Cidr = AclCidr.Ipv4(subnet.Ipv4CidrBlock),
					Traffic = ephemeralPortRange,
					RuleAction = Amazon.CDK.AWS.EC2.Action.ALLOW

				});
				pubElbPrivWebResponseRule++;
			}

			// Allow HTTP request from Internet.
			elbNacl.AddEntry("Incoming_HTTP_Internet", new CommonNetworkAclEntryOptions
			{
				RuleNumber = 100,
				Direction = TrafficDirection.INGRESS,
				Cidr = AclCidr.AnyIpv4(),
				Traffic = AclTraffic.TcpPort(80),
				RuleAction = Amazon.CDK.AWS.EC2.Action.ALLOW

			});

			// Allow HTTP request to private web subnets.
			var pubElbPrivWebRequestRule = 50;
			foreach (var subnet in webSubnets.Subnets)
			{
				elbNacl.AddEntry("Outgoing_HTTP_forward_" + subnet.Node.Id, new CommonNetworkAclEntryOptions
				{
					RuleNumber = pubElbPrivWebRequestRule,
					Direction = TrafficDirection.EGRESS,
					Cidr = AclCidr.Ipv4(subnet.Ipv4CidrBlock),
					Traffic = AclTraffic.TcpPort(80),
					RuleAction = Amazon.CDK.AWS.EC2.Action.ALLOW
				});
				pubElbPrivWebRequestRule++;
			}

			// Allow HTTP response to Internet.
			elbNacl.AddEntry("Outgoing_HTTP_response_Internet", new CommonNetworkAclEntryOptions
			{
				RuleNumber = 100,
				Direction = TrafficDirection.EGRESS,
				Cidr = AclCidr.AnyIpv4(),
				Traffic = ephemeralPortRange,
				RuleAction = Amazon.CDK.AWS.EC2.Action.ALLOW

			});

			#endregion Public web ELB

			#region Private web

			var privWebNaclName = "cdk_ec2_web_priv_nacl";
			var privWebNacl = new NetworkAcl(this.vpc, privWebNaclName,
			new NetworkAclProps
			{
				NetworkAclName = privWebNaclName,
				Vpc = this.vpc,
				SubnetSelection = new SubnetSelection { SubnetGroupName = this.privateWebSubnetName }
			});

			// Allow HTTP request from ELB subnets.
			var privWebPubElbRequestRule = 50;
			foreach (var subnet in elbSubnets.Subnets)
			{
				privWebNacl.AddEntry("Incoming_HTTP_forward_" + subnet.Node.Id, new CommonNetworkAclEntryOptions
				{
					RuleNumber = privWebPubElbRequestRule,
					Direction = TrafficDirection.INGRESS,
					Cidr = AclCidr.Ipv4(subnet.Ipv4CidrBlock),
					Traffic = AclTraffic.TcpPort(80),
					RuleAction = Amazon.CDK.AWS.EC2.Action.ALLOW

				});
				privWebPubElbRequestRule++;
			}

			// Allow private web servers' outgoing traffic responses from Internet.
			privWebNacl.AddEntry("Incoming_Internet_responses", new CommonNetworkAclEntryOptions
			{
				RuleNumber = 200,
				Direction = TrafficDirection.INGRESS,
				Cidr = AclCidr.AnyIpv4(),
				Traffic = ephemeralPortRange,
				RuleAction = Amazon.CDK.AWS.EC2.Action.ALLOW
			});

			// Allow HTTP request from Internet; OPTIONAL TEST
			privWebNacl.AddEntry("Incoming_HTTP_Internet", new CommonNetworkAclEntryOptions
			{
				RuleNumber = 500,
				Direction = TrafficDirection.INGRESS,
				Cidr = AclCidr.AnyIpv4(),
				Traffic = AclTraffic.TcpPort(80),
				RuleAction = Amazon.CDK.AWS.EC2.Action.ALLOW

			});

			// Allow SSH request from Internet; OPTIONAL TEST
			privWebNacl.AddEntry("Incoming_SSH_Internet", new CommonNetworkAclEntryOptions
			{
				RuleNumber = 501,
				Direction = TrafficDirection.INGRESS,
				Cidr = AclCidr.AnyIpv4(),
				Traffic = AclTraffic.TcpPort(22),
				RuleAction = Amazon.CDK.AWS.EC2.Action.ALLOW

			});

			// Allow HTTP response to ELB subnets.
			var privWebPubElbResponseRule = 50;
			foreach (var subnet in elbSubnets.Subnets)
			{
				privWebNacl.AddEntry("Outgoing_HTTP_response_" + subnet.Node.Id, new CommonNetworkAclEntryOptions
				{
					RuleNumber = privWebPubElbResponseRule,
					Direction = TrafficDirection.EGRESS,
					Cidr = AclCidr.Ipv4(subnet.Ipv4CidrBlock),
					Traffic = ephemeralPortRange,
					RuleAction = Amazon.CDK.AWS.EC2.Action.ALLOW

				});
				privWebPubElbResponseRule++;
			}

			// Allow HTTP/S requests to Internet.
			privWebNacl.AddEntry("Outgoing_HTTPS_request_Internet", new CommonNetworkAclEntryOptions
			{
				RuleNumber = 200,
				Direction = TrafficDirection.EGRESS,
				Cidr = AclCidr.AnyIpv4(),
				Traffic = AclTraffic.TcpPort(443),
				RuleAction = Amazon.CDK.AWS.EC2.Action.ALLOW
			});
			privWebNacl.AddEntry("Outgoing_HTTP_request_Internet", new CommonNetworkAclEntryOptions
			{
				RuleNumber = 201,
				Direction = TrafficDirection.EGRESS,
				Cidr = AclCidr.AnyIpv4(),
				Traffic = AclTraffic.TcpPort(80),
				RuleAction = Amazon.CDK.AWS.EC2.Action.ALLOW
			});

			// Allow (HTTP/SSH) responses back to Internet; OPTIONAL TEST
			privWebNacl.AddEntry("Outgoing_direct_response_Internet", new CommonNetworkAclEntryOptions
			{
				RuleNumber = 300,
				Direction = TrafficDirection.EGRESS,
				Cidr = AclCidr.AnyIpv4(),
				Traffic = ephemeralPortRange,
				RuleAction = Amazon.CDK.AWS.EC2.Action.ALLOW
			});

			#endregion Private web

		}

		private void TightenSecurityGroups()
		{
			this.pubElbSG = new SecurityGroup(this.vpc, this.pubElbSGName, new SecurityGroupProps
			{
				Vpc = this.vpc,
				SecurityGroupName = this.pubElbSGName,
				AllowAllOutbound = false
			});

			this.prviWebSG = new SecurityGroup(this.vpc, this.prviWebSGName, new SecurityGroupProps
			{
				Vpc = this.vpc,
				SecurityGroupName = this.prviWebSGName,
				AllowAllOutbound = false
			});


			// Public rules.
			this.pubElbSG.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(80), "Allow HTTP requests from Internet.");
			this.pubElbSG.AddEgressRule(this.prviWebSG, Port.Tcp(80), "Forward HTTP requests to internal web servers.");

			// Private rules.
			this.prviWebSG.AddIngressRule(this.pubElbSG, Port.Tcp(80), "Allow HTTP forwarding from load balancers.");
			this.prviWebSG.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(80), "Allow direct HTTP requests from Internet; OPTIONAL TEST");
			this.prviWebSG.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(22), "Allow SSH requests from Internet; OPTIONAL TEST");
			this.prviWebSG.AddEgressRule(Peer.AnyIpv4(), Port.Tcp(443), "Allow HTTPS requests to external web servers.");
			this.prviWebSG.AddEgressRule(Peer.AnyIpv4(), Port.Tcp(80), "Allow HTTP requests to external web servers.");
		}
		private void EstablishEC2()
		{
			this.EstablishWebAsg();
			this.EstablishWebElb();
		}

		private string[] LoadUserDataScript()
		{
			var userDataScriptFile = "EC2 user data script.sh";
			var userDataScriptPath = AppDomain.CurrentDomain.BaseDirectory + "/" + userDataScriptFile;
			if (!File.Exists(userDataScriptPath))
			{
				throw new FileNotFoundException("User Data script file for EC2 Launch Configuration not found in output directory: " + userDataScriptPath);
			}

			return File.ReadAllLines(userDataScriptPath);
		}

		private void EstablishWebAsg()
		{
			var userDataScript = this.LoadUserDataScript();
			var asgName = "AutoScalingWebServers";
			this.webServersAsg = new AutoScalingGroup(this, asgName, new AutoScalingGroupProps
			{
				AutoScalingGroupName = asgName,
				Vpc = this.vpc,
				VpcSubnets = new SubnetSelection { SubnetGroupName = this.privateWebSubnetName },
				AssociatePublicIpAddress = true,
				SecurityGroup = this.prviWebSG,
				InstanceType = InstanceType.Of(InstanceClass.BURSTABLE2, InstanceSize.MICRO),
				MachineImage = new AmazonLinuxImage
				 (new AmazonLinuxImageProps
				 {
					 Generation = AmazonLinuxGeneration.AMAZON_LINUX_2
				 }),
				KeyName = sshKey,
				DesiredCapacity = 1,
				MinCapacity = 1,
				MaxCapacity = 3,
				UpdatePolicy = UpdatePolicy.RollingUpdate(new RollingUpdateOptions
				{
					MinInstancesInService = 1,
					MaxBatchSize = 2
				})
			});
			this.webServersAsg.AddUserData(userDataScript);
		}

		private void EstablishWebElb()
		{
			var albName = "PublicWebLoadBalancer";
			var alb = new ApplicationLoadBalancer(this, albName, new ApplicationLoadBalancerProps
			{
				LoadBalancerName = albName,
				Vpc = this.vpc,
				VpcSubnets = new SubnetSelection { SubnetGroupName = this.publicElbSubnetName },
				SecurityGroup = this.pubElbSG,
				InternetFacing = true
			});

			var appListener = alb.AddListener("Listener", new BaseApplicationListenerProps
			{
				Port = 80,
				Protocol = ApplicationProtocol.HTTP,
				Open = true
			});

			appListener.AddTargets("WebServers", new AddApplicationTargetsProps
			{
				Targets = new[] { this.webServersAsg },
				Port = 80,
				HealthCheck = new Amazon.CDK.AWS.ElasticLoadBalancingV2.HealthCheck
				{
					Path = "/index.html",
					Interval = Duration.Minutes(2)
				}
			});
		}
	}
}