﻿using Puppet.Common.Exceptions;
using Puppet.Common.Devices;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using System.IO;
using Puppet.Common.Configuration;
using System.Net.WebSockets;
using System.Threading;
using Newtonsoft.Json;
using Puppet.Common.Events;
using Newtonsoft.Json.Linq;

namespace Puppet.Common.Services
{
    public class Hubitat : HomeAutomationPlatform
    {
        const string _appSettingsFileName = "appsettings.json";

        string _baseAddress;
        string _accessToken;
        string _websocketUrl;
        HttpClient _client;
        
        public Hubitat()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory()) // Directory where the json files are located
                .AddJsonFile(_appSettingsFileName, optional: false, reloadOnChange: true)
                .Build();
            HubitatOptions hubitatOptions = configuration.GetSection("Hubitat").Get<HubitatOptions>();

            _baseAddress = hubitatOptions.BaseUrl;
            _accessToken = hubitatOptions.AccessToken;
            _websocketUrl = hubitatOptions.EventWebsocketUrl;

            _client = new HttpClient();

            StateBag = new ConcurrentDictionary<string, object>();

            Task.Run(() => { HubitatEventWatcher(); });
        }

        public override async void DoAction(IDevice device, string action, string[] args = null)
        {
            // http://[IP Address]/apps/api/143/devices/[Device ID]/[Command]/[Secondary value]?access_token=

            string secondary = (args != null) ? $"/{args[0]}" : "";
            secondary = secondary.Replace(' ', '-').Replace('?', '.');

            Uri requestUri = new Uri($"{_baseAddress}/{device.Id}/{action.Trim()}{secondary.Trim()}?access_token={_accessToken}");
            Console.WriteLine($"{DateTime.Now} Sending request to Hubitat: {requestUri.ToString().Split('?')[0]}");
            var result = await _client.GetAsync(requestUri);
            result.EnsureSuccessStatusCode();
        }



        async void HubitatEventWatcher()
        {
            try
            {
                Console.WriteLine($"{DateTime.Now} Connecting to Hubitat...");
                var client = new ClientWebSocket();
                await client.ConnectAsync(new Uri(_websocketUrl), CancellationToken.None);
                Console.WriteLine($"{DateTime.Now} Websocket success! Watching for events.");
                ArraySegment<byte> buffer;
                while (client.State == WebSocketState.Open)
                {
                    buffer = new ArraySegment<byte>(new byte[1024 * 4]);
                    WebSocketReceiveResult reply = await client.ReceiveAsync(buffer, CancellationToken.None);
                    string json = System.Text.Encoding.Default.GetString(buffer.ToArray()).TrimEnd('\0');
                    HubEvent evt = JsonConvert.DeserializeObject<HubEvent>(json);
                    OnAutomationEvent(new AutomationEventEventArgs() { HubEvent = evt });
                }
            }
            catch(AggregateException ae)
            {
                ae.Handle((x) =>
                {
                    if (x is WebSocketException)
                    {
                        Console.WriteLine($"{DateTime.Now} Hubitat websocket error! {x.Message} -- Retrying in 5 seconds...");
                        Task.Delay(TimeSpan.FromSeconds(5)).Wait();
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                });

            }
            catch(Exception ex) 
            {
                // Something unknown went wrong. I don't care what because this method should run forever. Just mention it.
                Console.WriteLine($"{DateTime.Now} {ex} {ex.Message}");
            }
            finally
            {
                HubitatEventWatcher();
            }
        }
    }
}
