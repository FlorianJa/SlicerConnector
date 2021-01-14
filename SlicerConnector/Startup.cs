using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GcodeToMesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OctoPrintLib;
using OctoPrintLib.Messages;
using SlicingBroker;

namespace SlicerConnector
{
    public class Startup
    {
        private string slicerPath;

        public string ModelDownloadPath { get; private set; }
        public string GCodePath { get; private set; }
        public string MeshesPath { get; private set; }

        private OctoprintServer os;

        /// <summary>
        /// Domain name or IP of the OctoprintServer. Do not add protocol like http:// or https://. If a different Port than 80 is needed, specify it by :PORTNUMBER
        /// </summary>
        private string OctoPrintDomainNameOrIP;

        /// <summary>
        /// Application key for accessing th ocotprint
        /// </summary>
        private string OcotoprintApplicationKey;


        private string BasePath;

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
            var tmp = Configuration as ConfigurationRoot;

            OctoPrintDomainNameOrIP = configuration.GetValue<string>("OctoPrint:DomainNameOrIP");
            OcotoprintApplicationKey = configuration.GetValue<string>("OctoPrint:APIKey");
            BasePath = configuration.GetValue<string>("OctoPrint:BasePath");
            slicerPath = configuration.GetValue<string>("Slicer:Path");

            ModelDownloadPath = Path.Combine(BasePath, "Models");
            GCodePath = Path.Combine(BasePath, "GCode");
            MeshesPath = Path.Combine(BasePath, "Meshes");

            if (!File.Exists(slicerPath))
                throw new FileNotFoundException("The slicer application is not found in the given path");

            if (!Directory.Exists(ModelDownloadPath))
                Directory.CreateDirectory(ModelDownloadPath);


            os = new OctoprintServer(OctoPrintDomainNameOrIP, OcotoprintApplicationKey);
            os.FileAdded += Os_FileAdded;
            var x = os.GeneralOperations.Login();
            os.StartWebsocketAsync(x.name, x.session);
        }

        private async void Os_FileAdded(object sender, FileAddedEventArgs e)
        {
            if (e.Payload.type[0] == "model")
            {

                var downloadFullPath = Path.Combine(ModelDownloadPath, e.Payload.name);
                bool res = true;
                // check if the same exact file is available locally and can be consumed directly without downloading it again
                if (await CheckForFileDifference(downloadFullPath, e.Payload.path, e.Payload.storage))
                    res = await os.FileOperations.DownloadFileAsync(e.Payload.storage , e.Payload.path, downloadFullPath);

                if (res)
                {
                    SliceWithDefaultParameters(downloadFullPath);
                }
            }
        }

        private async Task<bool> CheckForFileDifference(string downloadFullPath, string fileName, string location = "local")
        {
            // file isn't on the disk 
            if (!System.IO.File.Exists(downloadFullPath))
                return true;
            else
            {
                // get file information from Octoprint
                var fileInfo = await os.FileOperations.GetFileInfoAsync(location, fileName);
                if (fileInfo != null)
                {
                    var octoSize = fileInfo.size;
                    var localSize = new FileInfo(downloadFullPath).Length;
                    if (octoSize != localSize)
                        return true;
                }

            }


            return false;

        }

        private void SliceWithDefaultParameters(string inputFile)
        {
            PrusaSlicerCLICommands commands = PrusaSlicerCLICommands.Default;
            commands.Output = GCodePath;
            if (!Directory.Exists(commands.Output))
            {
                Directory.CreateDirectory(commands.Output);
            }
            commands.File = inputFile;

            // if the same gcode was already found raise the filesliced event with that gcode file
            if (PrusaSlicerBroker.Slicing_Profile_Exists(GCodePath, commands, out string foundGcodePath))
                PrusaSlicerBroker_FileSliced(commands, new FileSlicedArgs(foundGcodePath));
            //if nothing was not found then slice the model and generate new gcode
            else
            {
                Task.Run(async () =>
                {

                    var prusaSlicerBroker = new PrusaSlicerBroker(slicerPath);
                    prusaSlicerBroker.FileSliced += PrusaSlicerBroker_FileSliced;
                    await prusaSlicerBroker.SliceAsync(commands);
                });
            }


        }

        private async void PrusaSlicerBroker_FileSliced(object sender, FileSlicedArgs e)
        {
            GenerateMeshFromGcode(e.SlicedFilePath);
            await UploadGCodeAsync(e.SlicedFilePath);
        }


        private async Task UploadGCodeAsync(string slicedFilePath)
        {
            await os.FileOperations.UploadFileAsync(slicedFilePath);

        }

        private void GenerateMeshFromGcode(string slicedFilePath)
        {
            var gcodeAnalyser = new GcodeAnalyser();
            //gcodeAnalyser.MeshGenrerated += GcodeAnalyser_MeshGenrerated;
            gcodeAnalyser.GenerateMeshFromGcode(slicedFilePath, MeshesPath);
        }

        //private void GcodeAnalyser_MeshGenrerated(object sender, bool e)
        //{

        //}

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
            var webSocketOptions = new WebSocketOptions()
            {
                KeepAliveInterval = TimeSpan.FromSeconds(120),
                ReceiveBufferSize = 4 * 1024
            };

            app.UseWebSockets(webSocketOptions);

            app.Use(async (context, next) =>
            {
                if (context.Request.Path == "/ws")
                {
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        using (WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync())
                        {
                            await HandleWebsocketConnection(context, webSocket);
                        }
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                    }
                }
                else
                {
                    await next();
                }
            });

        }

        /// <summary>
        /// Handle requests for the SlicerConnector websocket
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocket"></param>
        /// <returns></returns>
        private async Task HandleWebsocketConnection(HttpContext context, WebSocket webSocket)
        {
            var buffer = new byte[1024 * 4];
            WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            while (!result.CloseStatus.HasValue)
            {
                var receivedAsJson = Encoding.ASCII.GetString(buffer, 0, result.Count);

                //convert json to object
                PrusaSlicerCLICommands commands = JsonSerializer.Deserialize<PrusaSlicerCLICommands>(receivedAsJson, new JsonSerializerOptions() { IgnoreNullValues = true });
                commands.Output = GCodePath;
                if (!Directory.Exists(commands.Output))
                {
                    Directory.CreateDirectory(commands.Output);
                }
                if (commands.isValid())
                {
                    var prusaSlicerBroker = new PrusaSlicerBroker(slicerPath);
                    prusaSlicerBroker.FileSliced += PrusaSlicerBroker_FileSliced(webSocket);

                    prusaSlicerBroker.DataReceived += async (sender, args) =>
                    {
                        var slicingProgressMessage = new SlicingProgressMessage(args.Data).ToString();
                        var slicingProgressMessageBytes = Encoding.ASCII.GetBytes(slicingProgressMessage);
                        await webSocket.SendAsync(new ArraySegment<byte>(slicingProgressMessageBytes, 0, slicingProgressMessage.Length), WebSocketMessageType.Text, true, CancellationToken.None);
                    };

                    var localPath = Path.Combine(ModelDownloadPath, commands.File);
                    if (await CheckForFileDifference(localPath, commands.File))
                    {
                        var res = await os.FileOperations.DownloadFileAsync("local" , commands.File, localPath);

                        //downloading failed
                        if (!res)
                        {
                            var error = new ErrorMessage($"The requested file ({commands.File}) was not found on Octoprint").ToString();
                            var errorBytes = Encoding.ASCII.GetBytes(error);
                            await webSocket.SendAsync(new ArraySegment<byte>(errorBytes, 0, error.Length), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                        else
                        {
                            await SliceFile(webSocket, commands, localPath, prusaSlicerBroker);
                        }

                    }
                    // use the local file on the disk
                    else
                    {
                        await SliceFile(webSocket, commands, localPath, prusaSlicerBroker);
                    }
                }

                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            }

            await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
        }

        private async Task SliceFile(WebSocket webSocket, PrusaSlicerCLICommands commands, string localPath,
            PrusaSlicerBroker prusaSlicerBroker)
        {
            commands.File = localPath;
            if (PrusaSlicerBroker.Slicing_Profile_Exists(GCodePath, commands, out string foundGcodePath))
                PrusaSlicerBroker_FileSliced(webSocket).Invoke(commands, new FileSlicedArgs(foundGcodePath));

            else
            {
                await prusaSlicerBroker.SliceAsync(commands);
            }
        }

       

        private EventHandler<FileSlicedArgs> PrusaSlicerBroker_FileSliced(WebSocket websocket)
        {
            Action<object, FileSlicedArgs> action = async (sender, e) =>
            {
                var _websocket = websocket;
                var gcodeAnalyser = new GcodeAnalyser();
                gcodeAnalyser.MeshGenrerated += GcodeAnalyser_MeshGenrerated(_websocket, Path.GetFileNameWithoutExtension(e.SlicedFilePath));
                gcodeAnalyser.GenerateMeshFromGcode(e.SlicedFilePath, MeshesPath);
                await UploadGCodeAsync(e.SlicedFilePath);
            };

            return new EventHandler<FileSlicedArgs>(action);
        }


        private EventHandler<bool> GcodeAnalyser_MeshGenrerated(WebSocket websocket, string fileName)
        {
            Action<object, bool> action = (sender, e) =>
            {
                var _websocket = websocket;
                var _fileName = fileName;
                var args = new FileSlicedMessage("/api/Download/" + _fileName).ToString();

                var tmp = Encoding.ASCII.GetBytes(args);

                _websocket.SendAsync(new ArraySegment<byte>(tmp, 0, args.Length), WebSocketMessageType.Text, true, CancellationToken.None);
            };

            return new EventHandler<bool>(action);
        }
    }
}
