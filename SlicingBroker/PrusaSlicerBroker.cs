using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SlicingBroker
{
    /// <summary>
    /// class represents a connection with local Prusa slicer installed, used to slice a file given its path, with the given slicing parameters or the default ones
    /// </summary>
    public class PrusaSlicerBroker : ISlicerBroker
    {
        #region Slicing Process 
        private Process slicingProcess;
        private TaskCompletionSource<bool> eventHandled;
        public event EventHandler<FileSlicedArgs> FileSliced;
        #endregion

        public string SlicerPath { get; }
        public EventHandler<DataReceivedEventArgs> DataReceived;
        private static SemaphoreSlim semaphore = new SemaphoreSlim(1);
        private bool isBusy = false;

        public PrusaSlicerBroker(string localSlicerPath)
        {
            SlicerPath = localSlicerPath;
        }

        public async Task SliceAsync(PrusaSlicerCLICommands commands)
        {
            //request entry to the function if there is no one else using it
            await semaphore.WaitAsync();
            Console.WriteLine("Slicing started.");
            Console.WriteLine(commands.ToString());
            isBusy = true;
            var arguments = commands.ToString();
            eventHandled = new TaskCompletionSource<bool>();
            int errorsReceived = 0;
            using (slicingProcess = new Process())
            {
                try
                {
                    var psi = new ProcessStartInfo(SlicerPath)
                    {
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };

                    slicingProcess.Exited += (sender, args) =>
                    {
                        eventHandled.TrySetResult(true);
                        CheckFileSliced(commands, errorsReceived);

                        //release the locking of the function so that the other callers who are waiting can get to it one by one.
                        semaphore.Release();
                        Console.WriteLine("Slicing done.");
                        isBusy = false;
                    };
                    slicingProcess.OutputDataReceived += (sender, args) =>
                    {
                        if (!String.IsNullOrEmpty(args.Data))
                        {
                            OutputDataReceived(args);
                        }
                    };
                    slicingProcess.ErrorDataReceived += (sender, args) =>
                    {
                        if (!String.IsNullOrEmpty(args.Data))
                        {
                            errorsReceived++;
                            OutputDataReceived(args);
                        }
                    };

                    slicingProcess.StartInfo = psi;
                    slicingProcess.EnableRaisingEvents = true;

                    slicingProcess.Start();

                    // Asynchronously read the standard output of the spawned process.
                    // This raises OutputDataReceived events for each line of output.
                    slicingProcess.BeginOutputReadLine();
                    slicingProcess.BeginErrorReadLine();

                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }


                await Task.WhenAny(eventHandled.Task);
            }


            isBusy = false;
        }

        private void CheckFileSliced(PrusaSlicerCLICommands commands, int errorsReceived)
        {
            if (errorsReceived > 0)
                return;

            var slicedFilePath =
                Path.Combine(commands.Output, Path.GetFileNameWithoutExtension(commands.File) + ".gcode");
            if (!File.Exists(slicedFilePath))
                return;

            var fileSize = new FileInfo(slicedFilePath).Length;
            if (fileSize == 0)
                return;

            StoreSlicingProfile(slicedFilePath, commands);

            OnFileSliced(slicedFilePath);
        }

        public void OnFileSliced(string slicedFilePath)
        {
            FileSliced?.Invoke(this,
                new FileSlicedArgs(slicedFilePath));
        }

        private void StoreSlicingProfile(string slicedFilePath, PrusaSlicerCLICommands commands)
        {
            string tempfile = Path.GetTempFileName();
            string jsonString = JsonSerializer.Serialize(commands);
            string profileLine = "; SlicingProfileJson: " + jsonString;

            using (var writer = new StreamWriter(tempfile))
            using (var reader = new StreamReader(slicedFilePath))
            {
                writer.WriteLine(profileLine);
                while (!reader.EndOfStream)
                    writer.WriteLine(reader.ReadLine());
            }
            File.Copy(tempfile, slicedFilePath, true);
            File.Delete(tempfile);

        }

        private void OutputDataReceived(DataReceivedEventArgs args)
        {
            DataReceived?.Invoke(this, args);
        }

        public static bool Slicing_Profile_Exists(string slicingPath, PrusaSlicerCLICommands original, out string foundGcodePath)
        {
            foundGcodePath = "";

            var gcodeName = Path.Combine(slicingPath, Path.GetFileNameWithoutExtension(original.File) + ".gcode");
            if (!File.Exists(gcodeName))
                return false;

            var line1 = File.ReadLines(gcodeName).First();
            string searchString = "; SlicingProfileJson: ";
            if (!line1.StartsWith(searchString))
                return false;

            else
            {
               var jsonProfile= line1.Substring(searchString.Length);
               PrusaSlicerCLICommands found = JsonSerializer.Deserialize<PrusaSlicerCLICommands>(jsonProfile);

                if (original.LayerHeight != found.LayerHeight)
                    return false;
                if (original.SupportMaterial != found.SupportMaterial)
                    return false;
                if (original.FillDensity != found.FillDensity)
                    return false;
                
                foundGcodePath = gcodeName;
                return true;
            }

        }
    }
}
