using System.ComponentModel;
using System.ServiceProcess;
using System.Configuration.Install;

namespace LabstarService
{
    [RunInstaller(true)]
    public partial class Installer1 : System.Configuration.Install.Installer
    {
        ServiceInstaller serviceInstaller;
        ServiceProcessInstaller processInstaller;
        public Installer1()
        {
            InitializeComponent();
            serviceInstaller = new ServiceInstaller();
            processInstaller = new ServiceProcessInstaller();

            processInstaller.Account = ServiceAccount.LocalSystem;
            serviceInstaller.StartType = ServiceStartMode.Manual;
            serviceInstaller.ServiceName = "DDriver Labstar";
            serviceInstaller.Description = "DDriver ЮНОНА Labstar";
            Installers.Add(processInstaller);
            Installers.Add(serviceInstaller);
        }
    }
}
