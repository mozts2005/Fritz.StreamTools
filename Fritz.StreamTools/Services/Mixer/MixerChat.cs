﻿using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

// https://dev.mixer.com/reference/chat/index.html

namespace Fritz.StreamTools.Services.Mixer
{
	public class MixerChat : IMixerChat
	{
		readonly IConfiguration _config;
		readonly ILoggerFactory _loggerFactory;
		readonly HttpClient _client;
		readonly CancellationToken _shutdown;
		readonly ILogger _logger;
		int _myUserId;
		JsonRpcWebSocket _channel;

		public MixerChat(IConfiguration config, ILoggerFactory loggerFactory, HttpClient client, CancellationToken shutdown)
		{
			_config = config;
			_loggerFactory = loggerFactory;
			_client = client;
			_shutdown = shutdown;
			_logger = loggerFactory.CreateLogger<MixerChat>();
		}

		/// <summary>
		/// Raised each time a chat message is received
		/// </summary>
		public event EventHandler<ChatMessageEventArgs> OnChatMessage;

		/// <summary>
		/// Connect to the chat server, and join our channel
		/// </summary>
		/// <param name="userId">Our userId</param>
		/// <param name="channelId">Out channelId</param>
		/// <returns></returns>
		public async Task ConnectAndJoinAsync(int userId, int channelId)
		{
			// We need a access_token for this to succeed
			var token = _config["StreamServices:Mixer:Token"];
			if (string.IsNullOrEmpty(token)) return;

			_myUserId = userId;

			// Get chat authkey and chat endpoints
			var response = await _client.GetStringAsync($"chats/{channelId}");
			var doc = JToken.Parse(response);
			var chatAuthKey = doc["authkey"].Value<string>();
			var endpoints = doc["endpoints"].Values<string>().ToArray();

			_channel = new JsonRpcWebSocket(_loggerFactory, isChat: true);
			var endpointIndex = 1; // Skip 1st one, seems to fail often

			// Connect to the chat endpoint
			while (true)
			{
				string resolveUrl()
				{
					var endpoint = endpoints[endpointIndex];
					endpointIndex = (endpointIndex + 1) % endpoints.Length;
					return endpoints[endpointIndex];
				}

				if (await _channel.TryConnectAsync(resolveUrl, null, () =>
				{
					//
					// NOTE: Maybe we should request a new chat authKey if this if called due to reconnect ???
					//

					// Join the channel and send authkey
					return _channel.SendAsync("auth", channelId, userId, chatAuthKey);
				}))
				{
					break;
				}
			}

			_channel.OnEventReceived += Chat_OnEventReceived;
		}

		/// <summary>
		/// Send a chat message
		/// </summary>
		public async Task<bool> SendMessageAsync(string message)
		{
			var success = await _channel.SendAsync("msg", message);
			if (success) _logger.LogTrace($"Send message '{message}'");
			return success;
		}

		/// <summary>
		/// Send a whisper message to a user
		/// </summary>
		public async Task<bool> SendWhisperAsync(string userName, string message)
		{
			var success = await _channel.SendAsync("whisper", userName, message);
			if(success) _logger.LogTrace($"Send whisper to {userName} '{message}'");
			return success;
		}

		public async Task<bool> BanUserAsync(string userName)
		{
			//
			// NOTE: Cant find the command in the docs, so I dont known how to do this !!!!!!!
			//
			var success = await _channel.SendAsync("ban", userName);
			if (success) _logger.LogWarning($"BANNED {userName} on Mixer");
			return success;
		}

		public async Task<bool> TimeoutUserAsync(string userName, TimeSpan time)
		{
			var success = await _channel.SendAsync("timeout", userName, $"{time.Minutes}m{time.Seconds}s");
			if (success) _logger.LogWarning($"TIMEOUT {userName} on Mixer for {time}");
			return success;
		}

		/// <summary>
		/// Called when we receive a new event from the chat server
		/// </summary>
		private void Chat_OnEventReceived(object sender, EventEventArgs e)
		{
			if(e.Event == "ChatMessage")
			{
				var userId = e.Data["user_id"].Value<int>();
				var roles = e.Data["user_roles"].Values<string>();

				// Combine text from all elements
				var segments = e.Data["message"]["message"];
				var combinedText = string.Join("", segments.Where(x => x["text"] != null).Select(x => (string)x["text"]));

				OnChatMessage?.Invoke(this, new ChatMessageEventArgs
				{
					UserId = userId,
					UserName = e.Data["user_name"].Value<string>(),
					IsModerator = roles.Contains("Mod"),
					IsOwner = roles.Contains("Owner"),
					Message = combinedText
				});
			}
			else if(e.Event == "UserJoin")
			{
				_logger.LogTrace($"{e.Data["username"]} joined the Mixer channel");

			}
			else if (e.Event == "UserLeave")
			{
				_logger.LogTrace($"{e.Data["username"]} left the Mixer channel");

			}
		}
	}
}
