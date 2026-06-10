namespace CwcConfigurator.Domain;

public static class SignalFactory
{
    public static List<DigitalSignalConfig> CreateDigitalRows(int moduleCount, int portsPerModule)
    {
        var rows = new List<DigitalSignalConfig>(moduleCount * portsPerModule);
        for (var module = 0; module < moduleCount; module++)
        {
            for (var port = 0; port < portsPerModule; port++)
            {
                rows.Add(new DigitalSignalConfig
                {
                    ModuleNumber = module,
                    Port = port,
                    Config = SignalConfigState.Disabled,
                    Id = 0
                });
            }
        }

        return rows;
    }

    public static List<AnalogSignalConfig> CreateAnalogRows(int moduleCount, int portsPerModule)
    {
        var rows = new List<AnalogSignalConfig>(moduleCount * portsPerModule);
        for (var module = 0; module < moduleCount; module++)
        {
            for (var port = 0; port < portsPerModule; port++)
            {
                rows.Add(new AnalogSignalConfig
                {
                    ModuleNumber = module,
                    Port = port,
                    Config = SignalConfigState.Disabled,
                    CustomLimits = SignalConfigState.Disabled
                });
            }
        }

        return rows;
    }

    public static List<CounterConfig> CreateCounterRows(int moduleCount)
    {
        return Enumerable.Range(0, moduleCount)
            .Select(module => new CounterConfig
            {
                ModuleNumber = module,
                Config = SignalConfigState.Disabled
            })
            .ToList();
    }

    public static List<AnalogOutputConfig> CreateAnalogOutputRows(int moduleCount, int portsPerModule)
    {
        var rows = new List<AnalogOutputConfig>(moduleCount * portsPerModule);
        for (var module = 0; module < moduleCount; module++)
        {
            for (var port = 0; port < portsPerModule; port++)
            {
                rows.Add(new AnalogOutputConfig
                {
                    ModuleNumber = module,
                    Port = port,
                    Config = SignalConfigState.Disabled
                });
            }
        }

        return rows;
    }
}
