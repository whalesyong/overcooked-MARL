namespace Hpmv {
    public class Injector {
        private static readonly object sync = new object();
        private static InjectorServer server;

        public static InjectorServer Server
        {
            get
            {
                lock (sync)
                {
                    if (server == null)
                    {
                        server = new InjectorServer();
                        server.Start();
                    }
                    return server;
                }
            }
        }

        public static void Destroy()
        {
            lock (sync)
            {
                if (server == null)
                {
                    return;
                }

                server.Destroy();
                server = null;
            }
        }
    }
}
