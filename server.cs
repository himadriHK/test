using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CommAPI
{
	public class Server
	{
		TcpListener listener;
		int port;
		TcpClient client;
		Common commUtil;
		List<clientStatistics> clientList;
		Dictionary<string, Queue<Payload>> allClientDataQueue;
		Dictionary<string, payloadDispatch> allClientTasksTracker;
		public delegate void methodExecuteEvent(string taskId, string fromClient, string toClient);
		public delegate void methodResultEvent(string taskId, string fromClient, string toClient);
		public delegate void methodRegisterClient(string clientId);
		public event methodExecuteEvent ExecuteEvent;
		public event methodResultEvent ResultEvent;
		public event methodRegisterClient RegisterClientEvent;

		public Server(int port)
		{
			this.port = port;
			commUtil = new Common(AppDomain.CurrentDomain.BaseDirectory+@"server.bin");
			listener = new TcpListener(IPAddress.Any, port);
			listener.Start();
			allClientDataQueue = new Dictionary<string, Queue<Payload>>();
			allClientTasksTracker = new Dictionary<string, payloadDispatch>();
			clientList = new List<clientStatistics>();
			Thread acceptConnections = new Thread(processConnections);
			acceptConnections.Start();
		}

		private void processConnections()
		{
			while (true)
			{
				client = listener.AcceptTcpClient();
				if (client.Connected)
				{
					Console.WriteLine("Server Conected");
					ParameterizedThreadStart processMethod = new ParameterizedThreadStart(processClient);
					Thread clientThread = new Thread(processMethod);
					clientThread.Start(client);
				}
			}
		}

		private void pushPayloadToClient(string client,NetworkStream clientStream)
		{
			string clientId = (string)client;
			var clientQueue = allClientDataQueue[clientId];
			while(true)
				lock(clientQueue)
				{
					while(clientQueue.Count>0)
						commUtil.sendPacket(clientStream, clientQueue.Dequeue());
				}
		}

		private void processClient(object clientInfo)
		{
			TcpClient client = (TcpClient)clientInfo;
			NetworkStream networkStream = client.GetStream();
			clientStatistics stats = new clientStatistics(clientList);
			string clientId = "";
			//ParameterizedThreadStart pushPayloadsMethod = new ParameterizedThreadStart(pushPayloadToClient);
			Thread pushPayloadToClientThread = new Thread(() => pushPayloadToClient(clientId, networkStream));
			lock (clientList)
			{
				clientList.Add(stats);
				clientList.Sort();
			}
			while (true)
			{
				if (!string.IsNullOrEmpty(clientId) && pushPayloadToClientThread.ThreadState != System.Threading.ThreadState.Running)
					pushPayloadToClientThread.Start();

				if (networkStream.DataAvailable)
				{
					Payload payload = commUtil.getPacket(networkStream);
					switch (payload.command)
					{
						case CommandType.REQUEST:
						case CommandType.APPEND_REQUEST:
							sendToExecuteQueue(payload,clientId);
						break;

						case CommandType.RESULT:
						case CommandType.APPEND_RESULT:
							sendToResultQueue(payload,clientId);
						break;

						case CommandType.REGISTER_CLIENT:
							doRegisterClient(payload,out clientId);
						break;

						case CommandType.REGISTER_ASSEMBLY:
							doRegisterAssembly(payload);
						break;

						case CommandType.UPLOAD_ASSEMBLY:
						case CommandType.APPEND_ASSEMBLY:
							doSaveAssembly(payload);
						break;

						case CommandType.DOWNLOAD:
							if(!string.IsNullOrEmpty(clientId))
								doSendAssembly(payload,clientId);
						break;

						case CommandType.STATUS:
							updateStatus(payload, stats);
						break;
					}
					//Console.WriteLine(payload.clientId + " " + payload.command + " " + payload.cpuUsage + " " + payload.memUsage);
				}
			}
		}

		private void updateStatus(Payload payload, clientStatistics stats)
		{
			if (!allClientDataQueue.ContainsKey(payload.clientId))
				return;

			double load = (payload.cpuUsage + payload.memUsage) / 2;
			stats.Load = load;
			stats.Name = payload.clientId;
		}

		private void doSendAssembly(Payload payload,string clientId)
		{
			if(allClientDataQueue.ContainsKey(clientId))
			{
				byte[] assemblyBytes = commUtil.codeLoader.codeDictionary.readAssembly(payload.assemblyName);
				byte[][] assemblyBytePayloads = commUtil.splitBytes(assemblyBytes);

				Payload assemblyPayload = new Payload();
				assemblyPayload.command = CommandType.DOWNLOAD;
				assemblyPayload.clientId = clientId;
				assemblyPayload.assemblyName = payload.assemblyName;
				assemblyPayload.clientTime = DateTime.Now;
				assemblyPayload.assemblyBytes = assemblyBytePayloads[0];
				assemblyPayload.isAppend = assemblyBytePayloads.GetLength(0) > 1;
				assemblyPayload.remainingPayloads = assemblyBytePayloads.GetLength(0) - 1;
				var clientQueue = allClientDataQueue[clientId];
				clientQueue.Enqueue(assemblyPayload);

				for (int i = 1; i < assemblyBytePayloads.GetLength(0); i++)
				{
					Payload assemblyExtraPayload = new Payload();
					assemblyExtraPayload.command = CommandType.APPEND_ASSEMBLY;
					assemblyExtraPayload.clientId = clientId;
					assemblyExtraPayload.assemblyName = payload.assemblyName;
					assemblyExtraPayload.clientTime = DateTime.Now;
					assemblyExtraPayload.assemblyBytes = assemblyBytePayloads[i];
					assemblyExtraPayload.isAppend = (i != (assemblyBytePayloads.GetLength(0) - 1));
					assemblyExtraPayload.remainingPayloads = (assemblyBytePayloads.GetLength(0) -1)-i;
					clientQueue.Enqueue(assemblyExtraPayload);
				}
			}
		}

		private void doSaveAssembly(Payload payload)
		{
			commUtil.storeAssembly(payload.assemblyName, payload.assemblyBytes, payload.isAppend, payload.remainingPayloads);
			Debug.Print("size of "+ payload.assemblyName +":"+commUtil.codeLoader.codeDictionary.readAssembly(payload.assemblyName).Length.ToString());
		}

		private void doRegisterAssembly(Payload payload)
		{
			commUtil.storeAssembly(payload.assemblyName, new byte[0], true, payload.remainingPayloads);
		}

		private void doRegisterClient(Payload payload,out string clientId)
		{
			if (!allClientDataQueue.ContainsKey(payload.clientId))
				allClientDataQueue.Add(payload.clientId, new Queue<Payload>());
			clientId = payload.clientId;
			var e = RegisterClientEvent;
			if (e != null)
				e.Invoke(payload.clientId);
		}

		private void sendToResultQueue(Payload payload,string toClientId)
		{
			string fromClientId;
			string runId = payload.runId;
			if (allClientTasksTracker.ContainsKey(runId))
			{
				fromClientId = allClientTasksTracker[runId].fromClientId;
				var queue = allClientDataQueue[fromClientId];
				payload.runId = payload.runId.Replace(fromClientId, "");
				queue.Enqueue(payload);
				var e = ResultEvent;
				if (e != null)
					e(runId, toClientId, fromClientId);
			}
		}

		private void sendToExecuteQueue(Payload payload,string fromClientId)
		{
			string toClientId;
			string runId = fromClientId + payload.runId;
			if (allClientTasksTracker.ContainsKey(runId))
				toClientId = allClientTasksTracker[runId].toClientId;
			else
			{
				toClientId = clientList.First().Name;
				payloadDispatch newTask = new payloadDispatch();
				newTask.fromClientId = fromClientId;
				newTask.toClientId = toClientId;
				newTask.runId = runId;
				newTask.submitTime = payload.clientTime;
				allClientTasksTracker.Add(runId, newTask);
			}

			if(allClientDataQueue.ContainsKey(fromClientId) && allClientDataQueue.ContainsKey(toClientId))
			{
				payload.command = (payload.command == CommandType.REQUEST) ? CommandType.EXECUTE : CommandType.EXECUTE_APPEND;
				payload.runId = runId;
				var queue = allClientDataQueue[toClientId];
				var e = ExecuteEvent;
				if (e != null)
					e(runId, fromClientId, toClientId);
				queue.Enqueue(payload);
			}
		}
	}

	public class payloadDispatch
	{
		public string runId { get; set; }
		public string fromClientId { get; set; }
		public string toClientId { get; set; }
		public DateTime? submitTime { get; set; }
		public DateTime? outputTime { get; set; }
	}

	class clientStatistics : IComparable<clientStatistics>
	{
		List<clientStatistics> containerList;
		double load;
		public clientStatistics(List<clientStatistics> list)
		{
			containerList = list;
		}
		public string Name { get; set; }
		public double Load
		{
			get
			{
				return load;
			}

			set
			{
				load = value;
				lock (containerList)
				{
					containerList.Sort();
				}
			}
		}

		public int CompareTo(clientStatistics other)
		{
			return Load.CompareTo(other.Load);
		}
	}
}
