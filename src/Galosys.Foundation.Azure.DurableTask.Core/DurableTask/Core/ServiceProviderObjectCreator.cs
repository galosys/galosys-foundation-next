namespace DurableTask.Core
{
    internal class ServiceProviderObjectCreator<T>
        : ObjectCreator<T>
      where T : class
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly Type _implementationType;

        public ServiceProviderObjectCreator(
            IServiceProvider serviceProvider,
            Type implementationType)
        {
            _serviceProvider = serviceProvider;
            _implementationType = implementationType;

            Name = NameVersionHelper.GetDefaultName(_implementationType);
            Version = NameVersionHelper.GetDefaultVersion(_implementationType);
        }

        public override T Create()
        {
            var instance = _serviceProvider.GetService(_implementationType);
            if (instance is not null)
                return (T)instance;

            return (T)Activator.CreateInstance(_implementationType);
        }
    }
}
