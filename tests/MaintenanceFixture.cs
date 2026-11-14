using System;
using System.IO;
using Microsoft.Win32;

public static class MaintenanceFixture
{
    public static int Main(string[] args)
    {
        if(args.Length!=1||!args[0].StartsWith("PopupGuard-Test-")) return 2;
        Guid id; if(!Guid.TryParse(args[0].Substring("PopupGuard-Test-".Length),out id)) return 2;
        string root=AppDomain.CurrentDomain.BaseDirectory;
        if(!File.Exists(Path.Combine(root,"test-fixture-only.txt"))) return 2;
        using(var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64))
            hive.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\"+args[0],false);
        File.WriteAllText(Path.Combine(root,"uninstaller-ran.txt"),DateTime.UtcNow.ToString("o"));
        return 0;
    }
}
