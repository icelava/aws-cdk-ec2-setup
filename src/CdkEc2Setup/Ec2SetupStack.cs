using Amazon.CDK;
using Amazon.CDK.AWS.AutoScaling;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.SSM;
using System;
using System.IO;



namespace CdkEc2Setup
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

		private string webServerGroupName = "AutoScalingWebServers";
		private string sshKey = "ec2_exps";
		private string ec2RoleName = "cdk_ec2_role";
		private Role ec2InstanceRole;

		private StringParameter cwuaConfigParameter;
		private AutoScalingGroup webServersAsg;

		internal Ec2SetupStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
		{
			this.EstablishNetwork();
			this.EstablishIAM();
			this.EstablishSystemsManager();
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
				VpcId = this.vpc.VpcId
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

		private string[] LoadTextFile (string fileName, string fileDescription)
		{
			var filetPath = AppDomain.CurrentDomain.BaseDirectory + "/" + fileName;
			if (!File.Exists(filetPath))
			{
				throw new FileNotFoundException(fileDescription + " not found in output directory: " + filetPath);
			}

			return File.ReadAllLines(filetPath);
		}

		private void EstablishIAM()
		{
			this.ec2InstanceRole = new Role(this, this.ec2RoleName, new RoleProps
			{
				AssumedBy = new ServicePrincipal("ec2.amazonaws.com"),
				Description = "Role for CDK EC2 demo instances."
			});

			this.ec2InstanceRole.AddManagedPolicy(ManagedPolicy.FromAwsManagedPolicyName("CloudWatchAgentServerPolicy"));
			this.ec2InstanceRole.AddManagedPolicy(ManagedPolicy.FromAwsManagedPolicyName("AmazonSSMManagedInstanceCore"));
		}

		private void EstablishSystemsManager()
		{
			var cwuaConfig = this.LoadTextFile("CWUA_config.json", "CloudWatch Unified Agent configuration");
			// CWUA config files in SSM Parameter store must begin as "AmazonCloudWatch-".
			this.cwuaConfigParameter = new StringParameter(this, "AmazonCloudWatch-cdk-ec2-demo-config", new StringParameterProps
			{
				ParameterName = "AmazonCloudWatch-cdk-ec2-demo-config",
				SimpleName = true,
				Description = "Trimmed configuration to report memory usage.",
				StringValue = string.Join("\n", cwuaConfig),
				Tier = ParameterTier.STANDARD
			});

			this.cwuaConfigParameter.GrantRead(this.ec2InstanceRole);
		}

		private void EstablishEC2()
		{
			this.EstablishWebAsg();
			this.EstablishWebElb();
		}

		private string[] LoadUserDataScript()
		{
			return this.LoadTextFile("EC2_user_data_script.sh", "User Data script file for EC2 Launch Configuration");
		}

		private LaunchTemplate EstablishWebLaunchTemplate()
		{
			var userData = UserData.ForLinux();
			userData.AddCommands(this.LoadUserDataScript());

			var lt = new LaunchTemplate(this, this.webServerGroupName + "Template", new LaunchTemplateProps
			{
				LaunchTemplateName = this.webServerGroupName,
				InstanceType = InstanceType.Of(InstanceClass.BURSTABLE2, InstanceSize.MICRO),
				MachineImage = new AmazonLinuxImage
				 (new AmazonLinuxImageProps
				 {
					 Generation = AmazonLinuxGeneration.AMAZON_LINUX_2
				 }),
				SecurityGroup = this.prviWebSG,
				KeyName = sshKey,
				Role = this.ec2InstanceRole,
				UserData = userData
			});

			// User data script needs to reference SSM parameter for CWUA config file,
			// so ensure SSM parameter gets defined first before launch template.
			lt.Node.AddDependency(this.cwuaConfigParameter);

			return lt;
		}
		private void EstablishWebAsg()
		{
			var groupName = this.webServerGroupName + "Group";
			var launchTemplate = this.EstablishWebLaunchTemplate();
			this.webServersAsg = new AutoScalingGroup(this, groupName, new AutoScalingGroupProps
			{
				AutoScalingGroupName = groupName,
				Vpc = this.vpc,
				VpcSubnets = new SubnetSelection { SubnetGroupName = this.privateWebSubnetName },
				AssociatePublicIpAddress = true,
				DesiredCapacity = 1,
				MinCapacity = 1,
				MaxCapacity = 3,
				UpdatePolicy = UpdatePolicy.RollingUpdate(new RollingUpdateOptions
				{
					MinInstancesInService = 1,
					MaxBatchSize = 2
				}),

				// Mandatory parameters for ASG but redundant since LaunchTemplate has them.
				InstanceType = InstanceType.Of(InstanceClass.BURSTABLE2, InstanceSize.MICRO),
				MachineImage = new AmazonLinuxImage
				 (new AmazonLinuxImageProps
				 {
					 Generation = AmazonLinuxGeneration.AMAZON_LINUX_2
				 }),

				// These must remain otherwise the ASG creates its own set of SG and role.
				SecurityGroup = this.prviWebSG,
				Role = this.ec2InstanceRole
			});

			// CDK level-2 construct still does NOT support directly adding LaunchTemplate.
			// Add via level-1 Cfn construct.
			var cfnAsg = (CfnAutoScalingGroup)this.webServersAsg.Node.DefaultChild;

			// Get rid of the lingering LaunchConfiguration.
			cfnAsg.LaunchConfigurationName = null;
			this.webServersAsg.Node.TryRemoveChild("LaunchConfig");
			
			cfnAsg.LaunchTemplate = new CfnAutoScalingGroup.LaunchTemplateSpecificationProperty()
			{
				LaunchTemplateName = launchTemplate.LaunchTemplateName,
				LaunchTemplateId = launchTemplate.LaunchTemplateId,
				Version = launchTemplate.DefaultVersionNumber
			};


			// Launch configuration style; superceded by Launch template.
			//this.webServersAsg.AddUserData(this.LoadUserDataScript());
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