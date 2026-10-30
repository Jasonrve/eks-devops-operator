#!/usr/bin/env sh
# Seeds Floci with what a real environment already has: an EKS cluster and the permissions boundary policy.
# Usage: FLOCI=http://host.docker.internal:4566 CLUSTER=e2e sh local/seed-floci.sh   (needs docker; no AWS account)
set -eu
FLOCI="${FLOCI:-http://host.docker.internal:4566}"; CLUSTER="${CLUSTER:-e2e}"
ACCOUNT=111111111111   # Floci: the access key id is the account id
aws() { MSYS_NO_PATHCONV=1 docker run --rm --add-host host.docker.internal:host-gateway -e AWS_ACCESS_KEY_ID="$ACCOUNT" -e AWS_SECRET_ACCESS_KEY=test \
  -e AWS_DEFAULT_REGION=us-east-1 amazon/aws-cli --endpoint-url "$FLOCI" "$@"; }
BOUNDARY='{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":["s3:*","sqs:*","dynamodb:*","secretsmanager:*"],"Resource":"*"}]}'
aws iam create-policy --policy-name standard-workload --path /eks-devops-operator/ --policy-document "$BOUNDARY" >/dev/null 2>&1 || true
VPC=$(aws ec2 create-vpc --cidr-block 10.0.0.0/16 --query Vpc.VpcId --output text)
SUBNET=$(aws ec2 create-subnet --vpc-id "$VPC" --cidr-block 10.0.1.0/24 --query Subnet.SubnetId --output text)
aws iam create-role --role-name eks-cluster --assume-role-policy-document '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"eks.amazonaws.com"},"Action":"sts:AssumeRole"}]}' >/dev/null 2>&1 || true
aws eks create-cluster --name "$CLUSTER" --role-arn "arn:aws:iam::$ACCOUNT:role/eks-cluster" --resources-vpc-config "subnetIds=$SUBNET" >/dev/null
echo "seeded cluster '$CLUSTER' and the permissions boundary in account $ACCOUNT"
