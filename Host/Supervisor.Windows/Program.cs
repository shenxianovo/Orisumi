using System.ComponentModel;
using Orisumi.Desktop;

if (args is not ["--store", var root]) return 2;
try { return WindowsSupervisor.Run(root); }
catch (Exception error) when (error is IOException or ArgumentException or InvalidDataException or Win32Exception or UnauthorizedAccessException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
