yum install amazon-cloudwatch-agent -y
/opt/aws/amazon-cloudwatch-agent/bin/amazon-cloudwatch-agent-ctl -a fetch-config -m ec2 -s -c ssm:{ssmParameterName}