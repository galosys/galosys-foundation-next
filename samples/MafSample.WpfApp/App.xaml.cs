using CommunityToolkit.Mvvm.DependencyInjection;
using Galosys.Foundation.Wpf.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Configuration;
using System.Data;
using System.Windows;

namespace MafSample.WpfApp
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private IHost _host;

        public App()
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((ctx, services) =>
                {
                    services.AddCore(ctx.Configuration)  // 核心基础设施
                            .AddWpf()// MVVM + 导航 + 菜单 + 权限 + 自动更新
                            .AddWpfControls()
                            .AddWpfPro()
                            .AddHttpClient();            // HttpClient 工厂

                    services.Configure<MenuOptions>(ctx.Configuration.GetSection("Menu"));
                })
                .Build();

            Ioc.Default.ConfigureServices(_host.Services);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            var login = Ioc.Default.GetRequiredService<MainWindow>();
            login.Show();
            MainWindow = login;
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_host is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else
                _host.Dispose();
            base.OnExit(e);
        }
    }

}
