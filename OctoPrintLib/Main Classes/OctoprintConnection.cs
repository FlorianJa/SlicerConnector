using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace OctoPrintLib
{
    /// <summary>
    /// The base class for the different Trackers
    /// </summary>
    public class OctoprintConnection
    {
        protected OctoprintServer server { get; set; }
        public OctoprintConnection(OctoprintServer server)
        {
            this.server = server;
        }


        /// <summary>
        /// A Get request for any String using your Account
        /// </summary>
        /// <returns>The result as a String, doesn't handle Exceptions</returns>
        /// <param name="location">The url sub-address like "http://192.168.1.2/<paramref name="location"/>"</param>
        protected async Task<string> GetAsync(string location)
        {
            string strResponseValue = string.Empty;
            using (WebClient wc = new WebClient())
            {
                wc.Headers.Add("X-Api-Key", server.ApplicationKey);
                Stream downStream = await wc.OpenReadTaskAsync("http://" + server.DomainNmaeOrIp + "/" + location);
                using (StreamReader sr = new StreamReader(downStream))
                {
                    strResponseValue = await sr.ReadToEndAsync();
                }
                return strResponseValue;
            }
        }

        /// <summary>
        /// Posts a string with the rights of your Account to a given <paramref name="location"/>..
        /// </summary>
        /// <returns>The Result if any exists. Doesn't handle exceptions</returns>
        /// <param name="location">The url sub-address like "http://192.168.1.2/<paramref name="location"/>"</param>
        /// <param name="arguments">The string to post tp the address</param>
        protected async Task<string> PostStringAsync(string location, string arguments)
        {
            using (WebClient wc = new WebClient())
            {
                wc.Headers.Add("X-Api-Key", server.ApplicationKey);
                return await wc.UploadStringTaskAsync("http://" + server.DomainNmaeOrIp + "/" + location, arguments);
            }
        }

        /// <summary>
        /// Posts a JSON object as a string, uses JObject from Newtonsoft.Json to a given <paramref name="location"/>.
        /// </summary>
        /// <returns>The Result if any exists. Doesn't handle exceptions</returns>
        /// <param name="location">The url sub-address like "http://192.168.1.2/<paramref name="location"/>"</param>
        /// <param name="arguments">The Newtonsoft Jobject to post tp the address</param>
        protected string PostJson(string location, JObject arguments)
        {
            try
            {
                string strResponseValue = string.Empty;
                String argumentString = string.Empty;
                argumentString = JsonConvert.SerializeObject(arguments);
                HttpWebRequest
                    request = WebRequest.CreateHttp("http://" + server.DomainNmaeOrIp + "/" +
                                                    location); // + "?apikey=" + apiKey);
                request.Method = "POST";
                request.Headers["X-Api-Key"] = server.ApplicationKey;
                request.ContentType = "application/json";
                using (var streamWriter = new StreamWriter(request.GetRequestStream()))
                {
                    streamWriter.Write(argumentString);
                }

                HttpWebResponse httpResponse;
                httpResponse = (HttpWebResponse) request.GetResponse();

                using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
                {
                    strResponseValue = streamReader.ReadToEnd();
                }

                return strResponseValue;
            }
            catch (WebException e)
            {
                string msg =
                    "The Octoprint environment was not set up correctly. Consider checking that Octoprint is already running " +
                    "on the URl+port number indicated in the appsettings.json file under the tag 'Octoprint'. \n " +
                    "Also check if the API key under the same tag is valid. \n" +
                    $"Exception message: {e.Message} ";

                Console.WriteLine(msg);
                throw;
            }
        }

        /// <summary>
        /// Posts a Delete request to a given <paramref name="location"/>
        /// </summary>
        /// <returns>The Result if any, shouldn't return anything.</returns>
        /// <param name="location">The url sub-address like "http://192.168.1.2/<paramref name="location"/>"</param>
        protected async Task<string> DeleteAsync(string location)
        {
            string strResponseValue = string.Empty;
            HttpWebRequest request = WebRequest.CreateHttp("http://" + server.DomainNmaeOrIp + "/" + location);
            request.Method = "DELETE";
            request.Headers["X-Api-Key"] = server.ApplicationKey;
            HttpWebResponse httpResponse;
            httpResponse = (HttpWebResponse)request.GetResponse();
            using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
            {
                var result = await streamReader.ReadToEndAsync();
            }
            return strResponseValue;
        }

        
        protected async Task<string> PostMultipartAsync(string location, MultipartContent multipartContent)
        {
            var httpClient = new HttpClient();
            var headers = httpClient.DefaultRequestHeaders;

            headers.Add("X-Api-Key", server.ApplicationKey);
            Uri requestUri = new Uri("http://" + server.DomainNmaeOrIp + "/" + location);

            string responsebody;
            try
            {
                //Send the GET request
                var response = await httpClient.PostAsync(requestUri, multipartContent);
                responsebody = await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                responsebody = "Error: " + ex.HResult.ToString("X") + " Message: " + ex.Message;
            }
            return responsebody;
        }
               
    }
}