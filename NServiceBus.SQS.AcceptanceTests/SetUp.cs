﻿using Amazon;
using Amazon.Runtime;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NServiceBus.AcceptanceTests
{
	[SetUpFixture]
	public class SetUpAcceptanceTests
	{
	/*	[SetUp]
		public void SetUp()
		{
			Debug.WriteLine("Deleting every queue in Sydney!");
			using (var c = AWSClientFactory.CreateAmazonSQSClient(new EnvironmentVariablesAWSCredentials(), Amazon.RegionEndpoint.APSoutheast2))
			{
				var queues = c.ListQueues("");
				foreach (var q in queues.QueueUrls)
				{
					c.DeleteQueue( q );
				}
			}
			// SQS doesn't allow you to create queues immediately after deleting them; 
			// so let's just chill out for a while and let SQS sort itself out. 
			Thread.Sleep(TimeSpan.FromSeconds(61));
		} */
	}
}