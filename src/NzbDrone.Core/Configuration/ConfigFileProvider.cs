using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Update;


namespace NzbDrone.Core.Configuration
{
    public interface IConfigFileProvider : IHandleAsync<ApplicationStartedEvent>,
                                           IExecute<ResetApiKeyCommand>
    {
        Dictionary<string, object> GetConfigDictionary();
        void SaveConfigDictionary(Dictionary<string, object> configValues);

        string BindAddress { get; }
        int Port { get; }
        int SslPort { get; }
        bool EnableSsl { get; }
        bool LaunchBrowser { get; }
        AuthenticationType AuthenticationMethod { get; }
        bool AnalyticsEnabled { get; }
        string LogLevel { get; }
        string Branch { get; }
        string ApiKey { get; }
        string SslCertHash { get; }
        string UrlBase { get; }
        string UiFolder { get; }
        Boolean UpdateAutomatically { get; }
        UpdateMechanism UpdateMechanism { get; }
        String UpdateScriptPath { get; }
    }

    public class ConfigFileProvider : IConfigFileProvider
    {
        public const string CONFIG_ELEMENT_NAME = "Config";

        private readonly IEventAggregator _eventAggregator;
        private readonly IDiskProvider _diskProvider;
        private readonly ICached<string> _cache;

        private readonly string _configFile;
        private static readonly Regex HiddenCharacterRegex = new Regex("[^a-z0-9]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly object Mutex = new object();

        public ConfigFileProvider(IAppFolderInfo appFolderInfo,
                                  ICacheManager cacheManager,
                                  IEventAggregator eventAggregator,
                                  IDiskProvider diskProvider)
        {
            _cache = cacheManager.GetCache<string>(GetType());
            _eventAggregator = eventAggregator;
            _diskProvider = diskProvider;
            _configFile = appFolderInfo.GetConfigPath();
        }

        public Dictionary<string, object> GetConfigDictionary()
        {
            var dict = new Dictionary<string, object>(StringComparer.InvariantCultureIgnoreCase);

            var type = GetType();
            var properties = type.GetProperties();

            foreach (var propertyInfo in properties)
            {
                var value = propertyInfo.GetValue(this, null);

                dict.Add(propertyInfo.Name, value);
            }

            return dict;
        }

        public void SaveConfigDictionary(Dictionary<string, object> configValues)
        {
            _cache.Clear();

            var allWithDefaults = GetConfigDictionary();

            foreach (var configValue in configValues)
            {
                if (configValue.Key.Equals("ApiKey", StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                if (configValue.Key.Equals("SslCertHash", StringComparison.InvariantCultureIgnoreCase) && configValue.Value.ToString().IsNotNullOrWhiteSpace())
                {
                    SetValue(configValue.Key.FirstCharToUpper(), HiddenCharacterRegex.Replace(configValue.Value.ToString(), String.Empty));
                    continue;
                }

                object currentValue;
                allWithDefaults.TryGetValue(configValue.Key, out currentValue);
                if (currentValue == null) continue;

                var equal = configValue.Value.ToString().Equals(currentValue.ToString());

                if (!equal)
                {
                    SetValue(configValue.Key.FirstCharToUpper(), configValue.Value.ToString());
                }
            }

            _eventAggregator.PublishEvent(new ConfigFileSavedEvent());
        }

        public string BindAddress
        {
            get
            {
                const string defaultValue = "*";

                string bindAddress = GetValue("BindAddress", defaultValue);
                if (string.IsNullOrWhiteSpace(bindAddress))
                {
                    return defaultValue;
                }

                return bindAddress;
            }
        }

        public int Port
        {
            get { return GetValueInt("Port", 8989); }
        }

        public int SslPort
        {
            get { return GetValueInt("SslPort", 9898); }
        }

        public bool EnableSsl
        {
            get { return GetValueBoolean("EnableSsl", false); }
        }

        public bool LaunchBrowser
        {
            get { return GetValueBoolean("LaunchBrowser", true); }
        }

        public string ApiKey
        {
            get
            {
                return GetValue("ApiKey", GenerateApiKey());
            }
        }

        public AuthenticationType AuthenticationMethod
        {
            get
            {
                var enabled = GetValueBoolean("AuthenticationEnabled", false, false);

                if (enabled)
                {
                    SetValue("AuthenticationMethod", AuthenticationType.Basic);
                    return AuthenticationType.Basic;
                }
                
                return GetValueEnum("AuthenticationMethod", AuthenticationType.None);
            }
        }

        public bool AnalyticsEnabled
        {
            get
            {
                return GetValueBoolean("AnalyticsEnabled", true, persist: false);
            }
        }

        public string Branch
        {
            get { return GetValue("Branch", "master").ToLowerInvariant(); }
        }

        public string LogLevel
        {
            get { return GetValue("LogLevel", "Info"); }
        }

        public string SslCertHash
        {
            get { return GetValue("SslCertHash", ""); }
        }

        public string UrlBase
        {
            get
            {
                var urlBase = GetValue("UrlBase", "").Trim('/');

                if (urlBase.IsNullOrWhiteSpace())
                {
                    return urlBase;
                }

                return "/" + urlBase.Trim('/').ToLower();
            }
        }

        public string UiFolder
        {
            get
            {
                return GetValue("UiFolder", "UI", false);
            }
        }

        public bool UpdateAutomatically
        {
            get { return GetValueBoolean("UpdateAutomatically", false, false); }
        }

        public UpdateMechanism UpdateMechanism
        {
            get { return GetValueEnum("UpdateMechanism", UpdateMechanism.BuiltIn, false); }
        }

        public string UpdateScriptPath
        {
            get { return GetValue("UpdateScriptPath", "", false ); }
        }

        public int GetValueInt(string key, int defaultValue)
        {
            return Convert.ToInt32(GetValue(key, defaultValue));
        }

        public bool GetValueBoolean(string key, bool defaultValue, bool persist = true)
        {
            return Convert.ToBoolean(GetValue(key, defaultValue, persist));
        }

        public T GetValueEnum<T>(string key, T defaultValue, bool persist = true)
        {
            return (T)Enum.Parse(typeof(T), GetValue(key, defaultValue), persist);
        }

        public string GetValue(string key, object defaultValue, bool persist = true)
        {
            return _cache.Get(key, () =>
                {
                    var xDoc = LoadConfigFile();
                    var config = xDoc.Descendants(CONFIG_ELEMENT_NAME).Single();

                    var parentContainer = config;

                    var valueHolder = parentContainer.Descendants(key).ToList();

                    if (valueHolder.Count() == 1)
                    {
                        return valueHolder.First().Value.Trim();
                    }

                    //Save the value
                    if (persist)
                    {
                        SetValue(key, defaultValue);
                    }

                    //return the default value
                    return defaultValue.ToString();
                });
        }

        public void SetValue(string key, object value)
        {
            var valueString = value.ToString().Trim();
            var xDoc = LoadConfigFile();
            var config = xDoc.Descendants(CONFIG_ELEMENT_NAME).Single();

            var parentContainer = config;

            var keyHolder = parentContainer.Descendants(key);

            if (keyHolder.Count() != 1)
            {
                parentContainer.Add(new XElement(key, valueString));
            }

            else
            {
                parentContainer.Descendants(key).Single().Value = valueString;
            }

            _cache.Set(key, valueString);

            SaveConfigFile(xDoc);
        }

        public void SetValue(string key, Enum value)
        {
            SetValue(key, value.ToString().ToLower());
        }

        private void EnsureDefaultConfigFile()
        {
            if (!File.Exists(_configFile))
            {
                SaveConfigDictionary(GetConfigDictionary());
            }
        }

        private void DeleteOldValues()
        {
            var xDoc = LoadConfigFile();
            var config = xDoc.Descendants(CONFIG_ELEMENT_NAME).Single();

            var type = GetType();
            var properties = type.GetProperties();

            foreach (var configValue in config.Descendants().ToList())
            {
                var name = configValue.Name.LocalName;

                if (!properties.Any(p => p.Name == name))
                {
                    config.Descendants(name).Remove();
                }
            }

            SaveConfigFile(xDoc);
        }

        private XDocument LoadConfigFile()
        {
            try
            {
                lock (Mutex)
                {
                    if (_diskProvider.FileExists(_configFile))
                    {
                        return XDocument.Parse(_diskProvider.ReadAllText(_configFile));
                    }

                    var xDoc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"));
                    xDoc.Add(new XElement(CONFIG_ELEMENT_NAME));

                    return xDoc;
                }
            }

            catch (XmlException ex)
            {
                throw new InvalidConfigFileException(_configFile + " is invalid, please see the http://wiki.sonarr.tv for steps to resolve this issue.", ex);
            }
        }

        private void SaveConfigFile(XDocument xDoc)
        {
            lock (Mutex)
            {
                _diskProvider.WriteAllText(_configFile, xDoc.ToString());
            }
        }

        private string GenerateApiKey()
        {
            return Guid.NewGuid().ToString().Replace("-", "");
        }

        public void HandleAsync(ApplicationStartedEvent message)
        {
            EnsureDefaultConfigFile();
            DeleteOldValues();
        }

        public void Execute(ResetApiKeyCommand message)
        {
            SetValue("ApiKey", GenerateApiKey());
        }
    }
}
