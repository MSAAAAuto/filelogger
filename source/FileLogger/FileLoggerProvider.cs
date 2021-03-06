﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Karambolo.Extensions.Logging.File
{
    [ProviderAlias(Alias)]
    public class FileLoggerProvider : ILoggerProvider
    {
        public const string Alias = "File";

        readonly Dictionary<string, FileLogger> _loggers;

        IFileLoggerSettings _settingsRef;
        IDisposable _settingsChangeToken;
        bool _isDisposed;

        protected FileLoggerProvider(IFileLoggerContext context, IFileLoggerSettingsBase settings)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            Context = context;
            Settings = settings.ToImmutable();

            Processor = CreateProcessor(Settings);

            _loggers = new Dictionary<string, FileLogger>();
        }

        public FileLoggerProvider(IFileLoggerContext context, IFileLoggerSettings settings)
            : this(context, (IFileLoggerSettingsBase)settings)
        {
            _settingsRef = settings;

            _settingsChangeToken =
                settings.ChangeToken != null && !settings.ChangeToken.HasChanged ?
                settings.ChangeToken.RegisterChangeCallback(HandleSettingsChanged, null) :
                null;
        }

        public FileLoggerProvider(IFileLoggerContext context, IOptionsMonitor<FileLoggerOptions> options)
            : this(context, options != null ? options.CurrentValue : throw new ArgumentNullException(nameof(options)))
        {
            _settingsChangeToken = options.OnChange(HandleOptionsChanged);
        }

        public void Dispose()
        {
            lock (_loggers)
                if (!_isDisposed)
                {
                    DisposeCore();
                    _isDisposed = true;
                }
        }

        protected virtual void DisposeCore()
        {
            _settingsChangeToken?.Dispose();

            // blocking in Dispose() seems to be a design flaw, however ConsoleLoggerProcess.Dispose() implemented this way as well
            ResetProcessor(null);            
            Processor.Dispose();
        }

        protected IFileLoggerContext Context { get; }
        protected IFileLoggerSettingsBase Settings { get; private set; }
        protected FileLoggerProcessor Processor { get; }

        protected virtual FileLoggerProcessor CreateProcessor(IFileLoggerSettingsBase settings)
        {
            return new FileLoggerProcessor(Context, settings);
        }

        void ResetProcessor(IFileLoggerSettingsBase newSettings)
        {
            var timeoutTask = Task.Delay(1500);
            Task.WhenAny(Processor.CompleteAsync(newSettings), timeoutTask).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        protected virtual string GetFallbackFileName(string categoryName)
        {
            return Context.FallbackFileName;
        }

        bool HandleSettingsChangedCore(IFileLoggerSettingsBase settings)
        {
            lock (_loggers)
            {
                if (_isDisposed)
                    return false;

                Settings = settings.ToImmutable();

                foreach (var logger in _loggers.Values)
                    logger.Update(GetFallbackFileName(logger.CategoryName), Settings);
            }

            ResetProcessor(Settings);

            return true;
        }

        void HandleOptionsChanged(IFileLoggerSettingsBase options)
        {
            HandleSettingsChangedCore(options);
        }

        void HandleSettingsChanged(object state)
        {
            _settingsChangeToken.Dispose();

            _settingsRef = _settingsRef.Reload();

            var isNotDisposed = HandleSettingsChangedCore(_settingsRef);

            _settingsChangeToken =
                isNotDisposed && _settingsRef.ChangeToken != null && !_settingsRef.ChangeToken.HasChanged ?
                _settingsRef.ChangeToken.RegisterChangeCallback(HandleSettingsChanged, null) :
                null;
        }

        protected virtual FileLogger CreateLoggerCore(string categoryName)
        {
            return new FileLogger(categoryName, GetFallbackFileName(categoryName), Processor, Settings, Context.GetTimestamp);
        }

        public ILogger CreateLogger(string categoryName)
        {
            FileLogger logger;

            lock (_loggers)
            {
                if (_isDisposed)
                    throw new ObjectDisposedException(nameof(FileLoggerProvider));

                if (!_loggers.TryGetValue(categoryName, out logger))
                    _loggers.Add(categoryName, logger = CreateLoggerCore(categoryName));
            }

            return logger;
        }
    }
}
