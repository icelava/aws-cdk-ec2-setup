# For unknown reason, the bash shebang is not necessary when adding via CDK.

yum update -y
yum install httpd -y
systemctl start httpd.service
systemctl enable httpd

# Ubuntu style 
# apt update
# apt install apache2 -y
# systemctl enable apache2


AZ_ID=$(curl http://169.254.169.254/latest/meta-data/placement/availability-zone/)
MAC_ADDR=$(curl http://169.254.169.254/latest/meta-data/mac/)
IP_PRIVATE=$(curl http://169.254.169.254/latest/meta-data/local-ipv4/)
IP_PUBLIC=$(curl http://169.254.169.254/latest/meta-data/public-ipv4/)
PUBLIC_HOSTNAME=$(curl http://169.254.169.254/latest/meta-data/public-hostname/)
EC2_INST_TYPE=$(curl http://169.254.169.254/latest/meta-data/instance-type/)
AMI_ID=$(curl http://169.254.169.254/latest/meta-data/ami-id/)

echo -e "<html><ul><li>EC2 instance $EC2_INST_TYPE</li><li>AMI $AMI_ID</li><li>Availability zone $AZ_ID</li><li>MAC address $MAC_ADDR</li><li>Private IPv4 $IP_PRIVATE</li><li>Private hostname $(hostname)</li><li>Public IP $IP_PUBLIC</li><li>Public hostname $PUBLIC_HOSTNAME</li></ul><html>" > /var/www/html/index.html