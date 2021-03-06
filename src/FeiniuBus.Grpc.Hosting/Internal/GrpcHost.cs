﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FeiniuBus.Grpc.Hosting.Builder;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FeiniuBus.Grpc.Hosting.Internal
{
    internal class GrpcHost : IGrpcHost
    {
        private const string DefaultHost = "localhost";
        private const int DefaultPort = 4009;
        
        private ILogger<GrpcHost> _logger;
        private readonly IServiceProvider _hostingServiceProvider;
        private readonly List<Type> _serviceTypes;
        private readonly IConfiguration _config;
        private IServiceProvider _applicationServices;
        private readonly IServiceCollection _applicationServiceCollection;
        private IStartup _startup;
        private ApplicationLifetime _applicationLifetime;
        private bool _built;
        private bool _stopped;

        public GrpcHost(IServiceCollection appServices, IServiceProvider hostingServiceProvider, IConfiguration config,
            List<Type> serviceTypes)
        {
            _hostingServiceProvider = hostingServiceProvider ?? throw new ArgumentNullException(nameof(hostingServiceProvider));
            _applicationServiceCollection = appServices ?? throw new ArgumentNullException(nameof(appServices));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _serviceTypes = serviceTypes ?? throw new ArgumentNullException(nameof(serviceTypes));

            _applicationServiceCollection.AddSingleton<IApplicationLifetime, ApplicationLifetime>();
        }

        public void Dispose()
        {
            if (!_stopped)
            {
                try
                {
                    StopAsync().GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    _logger?.ServerShutdownException(e);
                }
            }
            
            (_applicationServices as IDisposable)?.Dispose();
            (_hostingServiceProvider as IDisposable)?.Dispose();
        }

        public IServiceProvider Services
        {
            get
            {
                EnsureApplicationServices();
                return _applicationServices;
            }
        }
        
        public Server Server { get; private set; }

        public void Initialize()
        {
            if (!_built)
            {
                BuildApplication();
            }
        }
        
        public void Start()
        {
            _logger = _applicationServices.GetRequiredService<ILogger<GrpcHost>>();
            _logger.Starting();
            
            Initialize();

            _applicationLifetime =
                _applicationServices.GetRequiredService<IApplicationLifetime>() as ApplicationLifetime;
            
            Server.Start();
            
            // Fire IApplicationLifetime.Started
            _applicationLifetime?.NotifyStarted();
            
            _logger.Started();
        }

        public async Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
            
            _logger?.Shutdown();

            // Fire IApplicationLifetime.Stopping
            _applicationLifetime.StopApplication();
            
            if (Server != null)
            {
                await Server.ShutdownAsync().ConfigureAwait(false);
            }
            
            // Fire IApplicationLifetime.Stopped
            _applicationLifetime.NotifyStopped();
        }

        private void EnsureApplicationServices()
        {
            if (_applicationServices == null)
            {
                EnsureStartup();
                _applicationServices = _startup.ConfigureServices(_applicationServiceCollection);
            }
        }

        private void BuildApplication()
        {
            EnsureApplicationServices();
            EnsureServer();

            var builderFactory = _applicationServices.GetRequiredService<IApplicationBuilderFactory>();
            var builder = builderFactory.CreateBuilder();
            builder.ApplicationServices = _applicationServices;

            Action<IApplicationBuilder> configure = _startup.Configure;
            configure(builder);

            _built = true;
        }

        private void EnsureStartup()
        {
            if (_startup != null)
            {
                return;
            }

            _startup = _hostingServiceProvider.GetRequiredService<IStartup>();
        }

        private void EnsureServer()
        {
            if (Server == null)
            {
                Server = new Server();
                
                var urls = _config[GrpcHostDefaults.ServerUrlsKey];
                if (string.IsNullOrEmpty(urls))
                {
                    Server.Ports.Add(new ServerPort(DefaultHost, DefaultPort, ServerCredentials.Insecure));
                }
                else
                {
                    foreach (var value in urls.Split(new []{','}, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = value.Split(new[] {':'}, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 1)
                        {
                            Server.Ports.Add(new ServerPort(parts[0], DefaultPort, ServerCredentials.Insecure));
                        }
                        else
                        {
                            Server.Ports.Add(new ServerPort(parts[0], Convert.ToInt32(parts[1]),
                                ServerCredentials.Insecure));
                        }
                    }
                }

                foreach (var serviceType in _serviceTypes)
                {
                    ServerServiceDefinition definition =
                        RpcServcieLoader.LoadService(_applicationServices, serviceType);

                    if (definition == null)
                    {
                        throw new InvalidOperationException($"Service type: {serviceType.FullName}'s definition is null");
                    }
                    
                    Server.Services.Add(definition);
                }
            }
        }        
    }
}