﻿using System.Linq;

namespace StartAzureBatch
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;
    using Microsoft.Azure.Batch;
    using Microsoft.Azure.Batch.Auth;
    using Microsoft.Azure.Batch.Common;
    using Microsoft.WindowsAzure.Storage;
    using Microsoft.WindowsAzure.Storage.Blob;
    using GraphView;

    public class StartAzureBatch
    {
        private readonly string queryString;

        // Batch account credentials
        private readonly string batchAccountName;
        private readonly string batchAccountKey;
        private readonly string batchAccountUrl;

        // Storage account credentials
        private readonly string storageConnectionString;

        // CosmosDB account credentials
        private readonly string docDBEndPoint;
        private readonly string docDBKey;
        private readonly string docDBDatabaseId;
        private readonly string docDBCollectionId;
        private readonly bool useReverseEdge;
        private readonly string partitionByKey;
        private readonly int spilledEdgeThresholdViagraphAPI;

        private readonly string poolId;
        private readonly string jobId;
        // When internode communication is enabled, 
        // nodes in Cloud Services Configuration pools can communicate with each other on ports greater than 1100, 
        // and Virtual Machine Configuration pools do not restrict traffic on any port.
        private readonly int port;

        // number of tasks.
        private readonly int parallelism;

        private readonly string outputContainerName;
        private readonly string appContainerName;

        private readonly string denpendencyPath;
        private readonly string exeName;

        // local path that stores downloaded output
        private readonly string outputPath;

        public StartAzureBatch()
        {
            this.queryString = "g.V()";

            this.batchAccountName = "";
            this.batchAccountKey = "";
            this.batchAccountUrl = "";

            this.storageConnectionString = "";

            this.docDBEndPoint = "";
            this.docDBKey = "";
            this.docDBDatabaseId = "GroupMatch";
            this.docDBCollectionId = "Modern";
            this.useReverseEdge = true;
            this.partitionByKey = "name";
            this.spilledEdgeThresholdViagraphAPI = 1;

            this.poolId = "GraphViewPool";
            this.jobId = "GraphViewJob";
            this.port = 6061;

            this.parallelism = 2;

            this.outputContainerName = "output";
            this.appContainerName = "application";

            this.denpendencyPath = "..\\..\\..\\GraphViewProgram\\bin\\Debug\\";
            this.exeName = "Program.exe";

            this.outputPath = "";
        }

        public static void Main(string[] args)
        {
            try
            {
                StartAzureBatch client = new StartAzureBatch();
                RunQueryAsync(client).Wait();
            }
            catch (AggregateException ae)
            {
                Console.WriteLine();
                Console.WriteLine("One or more exceptions occurred.");
                Console.WriteLine();

                PrintAggregateException(ae);
            }
            finally
            {
                Console.WriteLine();
                Console.WriteLine("Sample complete, hit ENTER to exit...");
                Console.ReadLine();
            }
        }

        private static async Task RunQueryAsync(StartAzureBatch client)
        {
            Console.WriteLine($"Query {client.queryString} start.");

            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(client.storageConnectionString);

            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
            await CreateContainerIfNotExistAsync(blobClient, client.appContainerName);
            await CreateContainerIfNotExistAsync(blobClient, client.outputContainerName);
            
            Console.WriteLine("[compile query] start");
            string compileStr = client.CompileQuery();
            Console.WriteLine("[compile query] finish");

            string compileResultPath = $"compileResult-{client.jobId}";
            File.WriteAllText(compileResultPath, compileStr);
            
            // Obtain a shared access signature that provides write access to the output container to which the tasks will upload their output.
            string outputContainerSasUrl = GetContainerSasUrl(blobClient, client.outputContainerName, SharedAccessBlobPermissions.Write);

            BatchSharedKeyCredentials cred = new BatchSharedKeyCredentials(client.batchAccountUrl, client.batchAccountName, client.batchAccountKey);
            using (BatchClient batchClient = BatchClient.Open(cred))
            {
                await client.CreatePoolIfNotExistAsync(batchClient);

                //          IP   , AffinityId
                List<Tuple<string, string>> nodeInfo =  client.AllocateComputeNode(batchClient);

                Console.WriteLine("[make partition plan] start");
                string partitionStr = client.MakePartitionPlan(nodeInfo);
                Console.WriteLine("[make partition plan] finish");

                string partitionPath = $"parititonPlan-{client.jobId}";
                File.WriteAllText(partitionPath, partitionStr);

                // Paths to the executable and its dependencies that will be executed by the tasks
                List<string> applicationFilePaths = new List<string>
                {
                    Path.Combine(client.denpendencyPath, client.exeName), // Program.exe
                    Path.Combine(client.denpendencyPath, "Microsoft.WindowsAzure.Storage.dll"),
                    Path.Combine(client.denpendencyPath, "DocumentDB.Spatial.Sql.dll"),
                    Path.Combine(client.denpendencyPath, "GraphView.dll"),
                    Path.Combine(client.denpendencyPath, "JsonServer.dll"),
                    Path.Combine(client.denpendencyPath, "Microsoft.Azure.Documents.Client.dll"),
                    Path.Combine(client.denpendencyPath, "Microsoft.Azure.Documents.ServiceInterop.dll"),
                    Path.Combine(client.denpendencyPath, "Microsoft.SqlServer.TransactSql.ScriptDom.dll"),
                    Path.Combine(client.denpendencyPath, "Newtonsoft.Json.dll"),
                    Path.Combine(client.denpendencyPath, client.exeName + ".config"), // "Program.exe.config"
                    compileResultPath,
                    partitionPath,
                };

                List<ResourceFile> resourceFiles = await UploadFilesToContainerAsync(blobClient, client.appContainerName, applicationFilePaths);

                try
                {
                    await client.CreateJobAsync(batchClient);
                }
                catch (Exception e)
                {
                    batchClient.JobOperations.DeleteJob(client.jobId);
                    System.Threading.Thread.Sleep(5000);
                    await client.CreateJobAsync(batchClient);
                }

                string[] args = { "-file", compileResultPath, partitionPath, outputContainerSasUrl };
                await client.AddTasksAsync(batchClient, nodeInfo, resourceFiles, args);

                await MonitorTasks(batchClient, client.jobId, TimeSpan.FromMinutes(1));

                await client.DownloadAndAggregateOutputAsync(blobClient);

                // Clean up Storage resources
                //await DeleteContainerAsync(blobClient, client.outputContainerName);
                //await DeleteContainerAsync(blobClient, client.appContainerName);

                // For Debug. Print stdout and stderr
                client.PrintTaskOutput(batchClient);

                await batchClient.JobOperations.DeleteJobAsync(client.jobId);
            }
        }

        private string CompileQuery()
        {
            GraphViewConnection connection = new GraphViewConnection(
                this.docDBEndPoint, this.docDBKey, this.docDBDatabaseId, this.docDBCollectionId,
                GraphType.GraphAPIOnly, this.useReverseEdge, this.spilledEdgeThresholdViagraphAPI, this.partitionByKey);
            GraphViewCommand command = new GraphViewCommand(connection);

            command.CommandText = this.queryString;
            return command.CompileAndSerialize();
        }

        private List<Tuple<string, string>> AllocateComputeNode(BatchClient batchClient)
        {
            List<Tuple<string, string>> nodeInfo = new List<Tuple<string, string>>();

            CloudPool pool = batchClient.PoolOperations.GetPool(this.poolId);
            foreach (ComputeNode node in pool.ListComputeNodes())
            {
                // todo : implement an algorithm to allocate node
                nodeInfo.Add(new Tuple<string, string>(node.IPAddress, node.AffinityId));

                if (nodeInfo.Count == this.parallelism)
                {
                    break;
                }
            }

            return nodeInfo;
        }

        private string MakePartitionPlan(List<Tuple<string, string>> nodeInfo)
        {
            List<PartitionPlan> plans = new List<PartitionPlan>();
            
            // For debug
            Debug.Assert(this.parallelism == 2);

            plans.Add(new PartitionPlan(
                "_partition", 
                PartitionMethod.CompareEntire, nodeInfo[0].Item1, 
                this.port, 
                new List<string>{"marko", "vadas", "lop"}));

            plans.Add(new PartitionPlan(
                "_partition",
                PartitionMethod.CompareEntire, nodeInfo[1].Item1,
                this.port,
                new List<string> { "josh", "ripple", "peter" }));

            return PartitionPlan.SerializePatitionPlans(plans);
        }

        // For Debug
        private void PrintTaskOutput(BatchClient batchClient)
        {
            for (int i = 0; i < this.parallelism; i++)
            {
                CloudTask task = batchClient.JobOperations.GetTask(this.jobId, i.ToString());
                string stdOut = task.GetNodeFile(Constants.StandardOutFileName).ReadAsString();
                string stdErr = task.GetNodeFile(Constants.StandardErrorFileName).ReadAsString();
                Console.WriteLine("---- stdout.txt ----taskId: " + i);
                Console.WriteLine(stdOut);
                Console.WriteLine("---- stderr.txt ----taskId: " + i);
                Console.WriteLine(stdErr);
                Console.WriteLine("------------------------------------");
            }
        }

        /// <summary>
        /// Creates a container with the specified name in Blob storage, unless a container with that name already exists.
        /// </summary>
        /// <param name="blobClient">A <see cref="Microsoft.WindowsAzure.Storage.Blob.CloudBlobClient"/>.</param>
        /// <param name="containerName">The name for the new container.</param>
        /// <returns>A <see cref="System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
        private static async Task CreateContainerIfNotExistAsync(CloudBlobClient blobClient, string containerName)
        {
            CloudBlobContainer container = blobClient.GetContainerReference(containerName);

            if (await container.CreateIfNotExistsAsync())
            {
                Console.WriteLine("Container [{0}] created.", containerName);
            }
            else
            {
                Console.WriteLine("Container [{0}] exists, skipping creation.", containerName);
            }
        }

        /// <summary>
        /// Returns a shared access signature (SAS) URL providing the specified permissions to the specified container.
        /// </summary>
        /// <param name="blobClient">A <see cref="Microsoft.WindowsAzure.Storage.Blob.CloudBlobClient"/>.</param>
        /// <param name="containerName">The name of the container for which a SAS URL should be obtained.</param>
        /// <param name="permissions">The permissions granted by the SAS URL.</param>
        /// <returns>A SAS URL providing the specified access to the container.</returns>
        /// <remarks>The SAS URL provided is valid for 2 hours from the time this method is called. The container must
        /// already exist within Azure Storage.</remarks>
        private static string GetContainerSasUrl(CloudBlobClient blobClient, string containerName, SharedAccessBlobPermissions permissions)
        {
            // Set the expiry time and permissions for the container access signature. In this case, no start time is specified,
            // so the shared access signature becomes valid immediately
            SharedAccessBlobPolicy sasConstraints = new SharedAccessBlobPolicy
            {
                SharedAccessExpiryTime = DateTime.UtcNow.AddHours(2),
                Permissions = permissions
            };
            
            // Generate the shared access signature on the container, setting the constraints directly on the signature
            CloudBlobContainer container = blobClient.GetContainerReference(containerName);
            string sasContainerToken = container.GetSharedAccessSignature(sasConstraints);

            // Return the URL string for the container, including the SAS token
            return String.Format("{0}{1}", container.Uri, sasContainerToken);
        }

        /// <summary>
        /// Uploads the specified files to the specified Blob container, returning a corresponding
        /// collection of <see cref="ResourceFile"/> objects appropriate for assigning to a task's
        /// <see cref="CloudTask.ResourceFiles"/> property.
        /// </summary>
        /// <param name="blobClient">A <see cref="Microsoft.WindowsAzure.Storage.Blob.CloudBlobClient"/>.</param>
        /// <param name="inputContainerName">The name of the blob storage container to which the files should be uploaded.</param>
        /// <param name="filePaths">A collection of paths of the files to be uploaded to the container.</param>
        /// <returns>A collection of <see cref="ResourceFile"/> objects.</returns>
        private static async Task<List<ResourceFile>> UploadFilesToContainerAsync(CloudBlobClient blobClient, string inputContainerName, List<string> filePaths)
        {
            List<ResourceFile> resourceFiles = new List<ResourceFile>();

            foreach (string filePath in filePaths)
            {
                resourceFiles.Add(await UploadFileToContainerAsync(blobClient, inputContainerName, filePath));
            }

            return resourceFiles;
        }

        /// <summary>
        /// Uploads the specified file to the specified Blob container.
        /// </summary>
        /// <param name="filePath">The full path to the file to upload to Storage.</param>
        /// <param name="blobClient">A <see cref="Microsoft.WindowsAzure.Storage.Blob.CloudBlobClient"/>.</param>
        /// <param name="containerName">The name of the blob storage container to which the file should be uploaded.</param>
        /// <returns>A <see cref="Microsoft.Azure.Batch.ResourceFile"/> instance representing the file within blob storage.</returns>
        private static async Task<ResourceFile> UploadFileToContainerAsync(CloudBlobClient blobClient, string containerName, string filePath)
        {
            Console.WriteLine("Uploading file {0} to container [{1}]...", filePath, containerName);

            string blobName = Path.GetFileName(filePath);

            CloudBlobContainer container = blobClient.GetContainerReference(containerName);
            CloudBlockBlob blobData = container.GetBlockBlobReference(blobName);
            await blobData.UploadFromFileAsync(filePath);
            
            // Set the expiry time and permissions for the blob shared access signature. In this case, no start time is specified,
            // so the shared access signature becomes valid immediately
            SharedAccessBlobPolicy sasConstraints = new SharedAccessBlobPolicy
            {
                SharedAccessExpiryTime = DateTime.UtcNow.AddHours(2),
                Permissions = SharedAccessBlobPermissions.Read
            };

            // Construct the SAS URL for blob
            string sasBlobToken = blobData.GetSharedAccessSignature(sasConstraints);
            string blobSasUri = String.Format("{0}{1}", blobData.Uri, sasBlobToken);

            return new ResourceFile(blobSasUri, blobName);
        }

        /// <summary>
        /// Downloads all files from the specified blob storage container to the specified directory.
        /// </summary>
        /// <param name="blobClient">A <see cref="CloudBlobClient"/>.</param>
        /// <param name="containerName">The name of the blob storage container containing the files to download.</param>
        /// <param name="directoryPath">The full path of the local directory to which the files should be downloaded.</param>
        /// <returns>A <see cref="System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
        private static async Task DownloadBlobsFromContainerAsync(CloudBlobClient blobClient, string containerName, string directoryPath)
        {
            Console.WriteLine("Downloading all files from container [{0}]...", containerName);

            // Retrieve a reference to a previously created container
            CloudBlobContainer container = blobClient.GetContainerReference(containerName);

            // Get a flat listing of all the block blobs in the specified container
            foreach (IListBlobItem item in container.ListBlobs(prefix: null, useFlatBlobListing: true))
            {
                // Retrieve reference to the current blob
                CloudBlob blob = (CloudBlob)item;

                // Save blob contents to a file in the specified folder
                string localOutputFile = Path.Combine(directoryPath, blob.Name);
                await blob.DownloadToFileAsync(localOutputFile, FileMode.Create);
            }

            Console.WriteLine("All files downloaded to {0}", directoryPath);
        }

        /// <summary>
        /// Deletes the container with the specified name from Blob storage, unless a container with that name does not exist.
        /// </summary>
        /// <param name="blobClient">A <see cref="Microsoft.WindowsAzure.Storage.Blob.CloudBlobClient"/>.</param>
        /// <param name="containerName">The name of the container to delete.</param>
        /// <returns>A <see cref="System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
        private static async Task DeleteContainerAsync(CloudBlobClient blobClient, string containerName)
        {
            CloudBlobContainer container = blobClient.GetContainerReference(containerName);

            if (await container.DeleteIfExistsAsync())
            {
                Console.WriteLine("Container [{0}] deleted.", containerName);
            }
            else
            {
                Console.WriteLine("Container [{0}] does not exist, skipping deletion.", containerName);
            }
        }

        /// <summary>
        /// Processes all exceptions inside an <see cref="AggregateException"/> and writes each inner exception to the console.
        /// </summary>
        /// <param name="aggregateException">The <see cref="AggregateException"/> to process.</param>
        public static void PrintAggregateException(AggregateException aggregateException)
        {
            // Flatten the aggregate and iterate over its inner exceptions, printing each
            foreach (Exception exception in aggregateException.Flatten().InnerExceptions)
            {
                Console.WriteLine(exception.ToString());
                Console.WriteLine();
            }
        }

        private async Task CreatePoolIfNotExistAsync(BatchClient batchClient)
        {
            CloudPool pool = null;
            try
            {
                Console.WriteLine("Creating pool [{0}]...", this.poolId);

                pool = batchClient.PoolOperations.CreatePool(
                    poolId: this.poolId,
                    targetLowPriorityComputeNodes: 0,
                    targetDedicatedComputeNodes: this.parallelism,
                    virtualMachineSize: "small",
                    cloudServiceConfiguration: new CloudServiceConfiguration(osFamily: "4"));   // Windows Server 2012 R2

                // When internode communication is enabled, 
                // nodes in Cloud Services Configuration pools can communicate with each other on ports greater than 1100, 
                // and Virtual Machine Configuration pools do not restrict traffic on any port.
                pool.InterComputeNodeCommunicationEnabled = true;

                await pool.CommitAsync();
            }
            catch (BatchException be)
            {
                // Swallow the specific error code PoolExists since that is expected if the pool already exists
                if (be.RequestInformation?.BatchError != null && be.RequestInformation.BatchError.Code == BatchErrorCodeStrings.PoolExists)
                {
                    Console.WriteLine("The pool {0} already existed when we tried to create it", this.poolId);
                    pool = batchClient.PoolOperations.GetPool(this.poolId);
                    Console.WriteLine("TargetDedicatedComputeNodes: " + pool.TargetDedicatedComputeNodes);
                    Console.WriteLine("TargetLowPriorityComputeNodes :" + pool.TargetLowPriorityComputeNodes);
                }
                else
                {
                    throw; // Any other exception is unexpected
                }
            }
        }

        /// <summary>
        /// Creates a job in the specified pool.
        /// </summary>
        /// <param name="batchClient">A <see cref="BatchClient"/>.</param>
        /// <param name="jobId">The id of the job to be created.</param>
        /// <param name="poolId">The id of the <see cref="CloudPool"/> in which to create the job.</param>
        /// <returns>A <see cref="System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
        private async Task CreateJobAsync(BatchClient batchClient)
        {
            Console.WriteLine("Creating job [{0}]...", this.jobId);

            CloudJob job = batchClient.JobOperations.CreateJob();
            job.Id = this.jobId;
            job.PoolInformation = new PoolInformation { PoolId = this.poolId };

            await job.CommitAsync();
        }

        /// <summary>
        /// Creates tasks to process each of the specified input files, and submits them to the
        /// specified job for execution.
        /// </summary>
        /// <param name="batchClient">A <see cref="BatchClient"/>.</param>
        /// <param name="jobId">The id of the job to which the tasks should be added.</param>
        /// <param name="applicationFiles">A collection of <see cref="ResourceFile"/> objects representing the program 
        /// (with dependencies and serialization data) to be executed on the compute nodes.</param>
        /// <param name="args"></param>
        /// <returns>A collection of the submitted tasks.</returns>
        private async Task<List<CloudTask>> AddTasksAsync(BatchClient batchClient, List<Tuple<string, string>> nodeInfo,
            List<ResourceFile> applicationFiles, string[] args)
        {
            Console.WriteLine("Adding task to job [{0}]...", this.jobId);
            Debug.Assert(args.Length == 4);
            // Create a collection to hold the tasks that we'll be adding to the job
            List<CloudTask> tasks = new List<CloudTask>();

            for (int i = 0; i < this.parallelism; i++)
            {
                string taskCommandLine = $"cmd /c %AZ_BATCH_TASK_WORKING_DIR%\\{this.exeName} " +
                    $"\"{args[0]}\" \"{args[1]}\" \"{args[2]}\" \"{args[3]}\"";
                CloudTask task = new CloudTask(i.ToString(), taskCommandLine);
                task.ResourceFiles = new List<ResourceFile>(applicationFiles);
                
                // specify compute node
                task.AffinityInformation = new AffinityInformation(nodeInfo[i].Item2);

                // set partition-plan-index
                if (task.EnvironmentSettings == null)
                {
                    task.EnvironmentSettings = new List<EnvironmentSetting>();
                }
                task.EnvironmentSettings.Add(new EnvironmentSetting("PARTITION_PLAN_INDEX", i.ToString()));

                // set task running in administrator level
                task.UserIdentity = new UserIdentity(new AutoUserSpecification(
                    elevationLevel: ElevationLevel.Admin, scope: AutoUserScope.Task));

                tasks.Add(task);
            }

            // Add the tasks as a collection opposed to a separate AddTask call for each. Bulk task submission
            // helps to ensure efficient underlying API calls to the Batch service.
            await batchClient.JobOperations.AddTaskAsync(this.jobId, tasks);

            return tasks;
        }

        private async Task DownloadAndAggregateOutputAsync(CloudBlobClient blobClient)
        {
            Console.WriteLine("Downloading all files from container [{0}]...", this.outputContainerName);

            CloudBlobContainer container = blobClient.GetContainerReference(this.outputContainerName);

            string outputFile = Path.Combine(this.outputPath, $"output-{this.jobId}");
            // If file exists, clear it; otherwise create an empty file.
            File.WriteAllText(outputFile, String.Empty);
            using (StreamWriter file = new StreamWriter(outputFile))
            {
                // Get a flat listing of all the block blobs in the specified container
                foreach (IListBlobItem item in container.ListBlobs(prefix: null, useFlatBlobListing: true))
                {
                    // Retrieve reference to the current blob
                    CloudBlob blob = (CloudBlob)item;

                    // Save blob contents to a file in the specified folder
                    string localOutputFile = Path.Combine(this.outputPath, blob.Name);
                    await blob.DownloadToFileAsync(localOutputFile, FileMode.Create);

                    // write result to aggregate file
                    string text = File.ReadAllText(localOutputFile);
                    file.Write(text);
                }
            }

            Console.WriteLine("All files downloaded to {0}", this.outputPath);
            Console.WriteLine("Aggregation File create in {0}. The content is as follows:", outputFile);
            Console.Write(File.ReadAllText(outputFile));
        }

        /// <summary>
        /// Monitors the specified tasks for completion and returns a value indicating whether all tasks completed successfully
        /// within the timeout period.
        /// </summary>
        /// <param name="batchClient">A <see cref="BatchClient"/>.</param>
        /// <param name="jobId">The id of the job containing the tasks that should be monitored.</param>
        /// <param name="timeout">The period of time to wait for the tasks to reach the completed state.</param>
        /// <returns><c>true</c> if all tasks in the specified job completed with an exit code of 0 within the specified timeout period, otherwise <c>false</c>.</returns>
        private static async Task<bool> MonitorTasks(BatchClient batchClient, string jobId, TimeSpan timeout)
        {
            bool allTasksSuccessful = true;
            const string successMessage = "All tasks reached state Completed.";
            const string failureMessage = "One or more tasks failed to reach the Completed state within the timeout period.";

            // Obtain the collection of tasks currently managed by the job. Note that we use a detail level to
            // specify that only the "id" property of each task should be populated. Using a detail level for
            // all list operations helps to lower response time from the Batch service.
            ODATADetailLevel detail = new ODATADetailLevel(selectClause: "id");
            List<CloudTask> tasks = await batchClient.JobOperations.ListTasks(jobId, detail).ToListAsync();

            Console.WriteLine("Awaiting task completion, timeout in {0}...", timeout.ToString());

            // We use a TaskStateMonitor to monitor the state of our tasks. In this case, we will wait for all tasks to
            // reach the Completed state.
            TaskStateMonitor taskStateMonitor = batchClient.Utilities.CreateTaskStateMonitor();
            try
            {
                await taskStateMonitor.WhenAll(tasks, TaskState.Completed, timeout);
            }
            catch (TimeoutException)
            {
                Console.WriteLine(failureMessage);
                await batchClient.JobOperations.TerminateJobAsync(jobId, failureMessage);
                return false;
            }

            await batchClient.JobOperations.TerminateJobAsync(jobId, successMessage);

            // All tasks have reached the "Completed" state, however, this does not guarantee all tasks completed successfully.
            // Here we further check each task's ExecutionInfo property to ensure that it did not encounter a scheduling error
            // or return a non-zero exit code.

            // Update the detail level to populate only the task id and executionInfo properties.
            // We refresh the tasks below, and need only this information for each task.
            detail.SelectClause = "id, executionInfo";

            foreach (CloudTask task in tasks)
            {
                // Populate the task's properties with the latest info from the Batch service
                await task.RefreshAsync(detail);

                if (task.ExecutionInformation.Result == TaskExecutionResult.Failure)
                {
                    // A task with failure information set indicates there was a problem with the task. It is important to note that
                    // the task's state can be "Completed," yet still have encountered a failure.

                    allTasksSuccessful = false;

                    Console.WriteLine("WARNING: Task [{0}] encountered a failure: {1}", task.Id, task.ExecutionInformation.FailureInformation.Message);
                    if (task.ExecutionInformation.ExitCode != 0)
                    {
                        // A non-zero exit code may indicate that the application executed by the task encountered an error
                        // during execution. As not every application returns non-zero on failure by default (e.g. robocopy),
                        // your implementation of error checking may differ from this example.

                        Console.WriteLine("WARNING: Task [{0}] returned a non-zero exit code - this may indicate task execution or completion failure.", task.Id);
                    }
                }
            }

            if (allTasksSuccessful)
            {
                Console.WriteLine("Success! All tasks completed successfully within the specified timeout period.");
            }

            return allTasksSuccessful;
        }

        // For Debug
        private void DeletePool()
        {
            BatchSharedKeyCredentials cred = new BatchSharedKeyCredentials(this.batchAccountUrl, this.batchAccountName, this.batchAccountKey);
            using (BatchClient batchClient = BatchClient.Open(cred))
            {
                batchClient.PoolOperations.DeletePool(this.poolId);
            }
        }

        // For Debug
        private void DeleteJob()
        {
            BatchSharedKeyCredentials cred = new BatchSharedKeyCredentials(this.batchAccountUrl, this.batchAccountName, this.batchAccountKey);
            using (BatchClient batchClient = BatchClient.Open(cred))
            {
                batchClient.JobOperations.DeleteJob(this.jobId);
            }
        }

        // For Debug
        private void DeletePoolAndJob()
        {
            BatchSharedKeyCredentials cred = new BatchSharedKeyCredentials(this.batchAccountUrl, this.batchAccountName, this.batchAccountKey);
            using (BatchClient batchClient = BatchClient.Open(cred))
            {
                batchClient.JobOperations.DeleteJob(this.jobId);
                batchClient.PoolOperations.DeletePool(this.poolId);
            }
        }
    }
}