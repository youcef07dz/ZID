namespace ZID
{
    internal delegate IDownloadEngine EngineFactory();

    // Owns the single shared engine instance and its start/error state. The
    // form still decides when to reconcile, because that needs saved state.
    internal sealed class EngineHost
    {
        private readonly object _sync = new object();
        private readonly EngineFactory _factory;
        private IDownloadEngine _engine;

        internal string LastError = "";

        internal EngineHost(string exe, string tools, string certs)
            : this(new EngineFactory(delegate { return new Aria2Engine(exe, tools, certs); }))
        {
        }

        // Test seam: lets FakeEngine stand in for the real adapter.
        internal EngineHost(EngineFactory factory)
        {
            _factory = factory;
        }

        internal IDownloadEngine Current
        {
            get { return _engine; }
        }

        internal bool Ensure()
        {
            IDownloadEngine engine = _engine;
            if (engine == null)
            {
                lock (_sync)
                {
                    if (_engine == null) _engine = _factory();
                    engine = _engine;
                }
            }
            if (engine.EnsureStarted()) return true;
            LastError = engine.LastError;
            return false;
        }
    }
}
