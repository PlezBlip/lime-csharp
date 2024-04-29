﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lime.Client.TestConsole.Macros;
using Lime.Client.TestConsole.Mvvm;
using Lime.Client.TestConsole.Properties;
using Lime.Protocol;
using Lime.Protocol.Network;
using Lime.Protocol.Serialization;
using Lime.Protocol.Serialization.Newtonsoft;
using Lime.Transport.Tcp;
using Lime.Transport.WebSocket;

namespace Lime.Client.TestConsole.ViewModels
{
    public class SessionViewModel : ObservableObject, ITraceWriter
    {
        private readonly TimeSpan _operationTimeout;

        public SessionViewModel()
        {
            _operationTimeout = TimeSpan.FromSeconds(15);

            // Collections
            Envelopes = new ObservableCollectionEx<EnvelopeViewModel>();
            Variables = new ObservableCollectionEx<VariableViewModel>();
            Templates = new ObservableCollectionEx<TemplateViewModel>();
            Macros = new ObservableCollectionEx<MacroViewModel>();
            StatusMessages = new ObservableCollectionEx<StatusMessageViewModel>();
            Profiles = new ObservableCollectionEx<ProfileViewModel>();

            // Commands
            OpenTransportCommand = new AsyncRelayCommand(OpenTransportAsync, CanOpenTransport);
            CloseTransportCommand = new AsyncRelayCommand(CloseTransportAsync, CanCloseTransport);
            SendCommand = new AsyncRelayCommand<string>(SendAsync, CanSend);
            ClearTraceCommand = new RelayCommand(ClearTrace);
            IndentCommand = new RelayCommand(Indent, CanIndent);
            ValidateCommand = new RelayCommand(Validate, CanValidate);
            LoadTemplateCommand = new RelayCommand(LoadTemplate, CanLoadTemplate);
            ParseCommand = new RelayCommand(Parse, CanParse);
            LoadProfileCommand = new RelayCommand(LoadProfile);
            SaveProfileCommand = new RelayCommand(SaveProfile);
            DeleteElementProfileCommand = new RelayCommand(DeleteProfile, CanDeleteProfile);

            // Defaults
            DarkMode = false;
            Host = "net.tcp://iris.limeprotocol.org:55321";
            ClientCertificateThumbprint = Settings.Default.LastCertificateThumbprint;
            ClearAfterSent = true;
            ParseBeforeSend = true;
            IgnoreParsingErrors = false;
            Repeat = false;
            RepeatTimes = 1;

            if (!UIHelper.IsInDesignMode)
            {
                LoadHost();
                LoadVariables();
                LoadProfiles();
                LoadTemplates();
                LoadMacros();
                LoadConfigurations();
            }

            ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, errors) =>
            {
                if (errors != SslPolicyErrors.None)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    AddStatusMessage($"TLS server certificate validation errors: {errors}", true));
                }
                return true;
            };
        }

        private ITcpClient _tcpClient;

        public ITcpClient TcpClient
        {
            get { return _tcpClient; }
            set
            {
                _tcpClient = value;
                OnPropertyChanged(nameof(TcpClient));
            }
        }

        private ITransport _transport;

        public ITransport Transport
        {
            get { return _transport; }
            set
            {
                _transport = value;
                OnPropertyChanged(nameof(Transport));
            }
        }

        private bool _isBusy;

        public bool IsBusy
        {
            get { return _isBusy; }
            set
            {
                _isBusy = value;
                OnPropertyChanged(nameof(IsBusy));

                OpenTransportCommand.NotifyCanExecuteChanged();
                CloseTransportCommand.NotifyCanExecuteChanged();
                SendCommand.NotifyCanExecuteChanged();
            }
        }

        private bool _darkMode;

        public bool DarkMode
        {
            get { return _darkMode; }
            set
            {
                _darkMode = value;
                OnPropertyChanged(nameof(DarkMode));
            }
        }

        private string _statusMessage;

        public string StatusMessage
        {
            get { return _statusMessage; }
            set
            {
                _statusMessage = value;
                OnPropertyChanged(nameof(StatusMessage));
            }
        }

        private ObservableCollectionEx<StatusMessageViewModel> _statusMessages;

        public ObservableCollectionEx<StatusMessageViewModel> StatusMessages
        {
            get { return _statusMessages; }
            set
            {
                _statusMessages = value;
                OnPropertyChanged(nameof(StatusMessages));
            }
        }

        private string _clientCertificateThumbprint;

        public string ClientCertificateThumbprint
        {
            get { return _clientCertificateThumbprint; }
            set
            {
                _clientCertificateThumbprint = value;
                Settings.Default.LastCertificateThumbprint = value;
                OnPropertyChanged(nameof(ClientCertificateThumbprint));
            }
        }

        private SessionState _lastSessionState;

        public SessionState LastSessionState
        {
            get { return _lastSessionState; }
            set
            {
                _lastSessionState = value;
                OnPropertyChanged(nameof(LastSessionState));
            }
        }

        private Node _localNode;

        public Node LocalNode
        {
            get { return _localNode; }
            set
            {
                _localNode = value;
                OnPropertyChanged(nameof(LocalNode));
            }
        }

        private Node _remoteNode;

        public Node RemoteNode
        {
            get { return _remoteNode; }
            set
            {
                _remoteNode = value;
                OnPropertyChanged(nameof(RemoteNode));
            }
        }

        private Event? _lastNotificationEvent;

        public Event? LastNotificationEvent
        {
            get { return _lastNotificationEvent; }
            set
            {
                _lastNotificationEvent = value;
                OnPropertyChanged(nameof(LastNotificationEvent));
            }
        }

        private string _inputJson;

        public string InputJson
        {
            get { return _inputJson; }
            set
            {
                _inputJson = value;
                OnPropertyChanged(nameof(InputJson));

                SendCommand.NotifyCanExecuteChanged();
                IndentCommand.NotifyCanExecuteChanged();
                ValidateCommand.NotifyCanExecuteChanged();
                ParseCommand.NotifyCanExecuteChanged();
            }
        }

        public string JsonToSend { get; set; }

        private string _host;

        private Uri _hostUri;

        public string Host
        {
            get { return _host; }
            set
            {
                _host = value;
                OnPropertyChanged(nameof(Host));

                OpenTransportCommand.NotifyCanExecuteChanged();
            }
        }

        private string _profileName;

        public string ProfileName
        {
            get { return _profileName; }
            set
            {
                _profileName = value;
                OnPropertyChanged(nameof(ProfileName));
            }
        }

        private bool _isConnected;

        public bool IsConnected
        {
            get { return _isConnected; }
            set
            {
                _isConnected = value;
                OnPropertyChanged(nameof(IsConnected));

                OpenTransportCommand.NotifyCanExecuteChanged();
                CloseTransportCommand.NotifyCanExecuteChanged();
                SendCommand.NotifyCanExecuteChanged();
            }
        }

        private ObservableCollectionEx<EnvelopeViewModel> _envelopes;

        public ObservableCollectionEx<EnvelopeViewModel> Envelopes
        {
            get { return _envelopes; }
            set
            {
                _envelopes = value;
                OnPropertyChanged(nameof(Envelopes));

                if (_envelopes != null)
                {
                    EnvelopesView = CollectionViewSource.GetDefaultView(_envelopes);
                    EnvelopesView.Filter = new Predicate<object>(o =>
                    {
                        var envelopeViewModel = o as EnvelopeViewModel;

                        return envelopeViewModel != null &&
                               (ShowRawValues || !envelopeViewModel.IsRaw);
                    });
                }
            }
        }

        private ICollectionView _envelopesView;

        public ICollectionView EnvelopesView
        {
            get { return _envelopesView; }
            set
            {
                _envelopesView = value;
                OnPropertyChanged(nameof(EnvelopesView));
            }
        }

        private bool _showRawValues;

        public bool ShowRawValues
        {
            get { return _showRawValues; }
            set
            {
                _showRawValues = value;
                OnPropertyChanged(nameof(ShowRawValues));

                if (EnvelopesView != null)
                {
                    EnvelopesView.Refresh();
                }
            }
        }

        private bool _sendAsRaw;

        public bool SendAsRaw
        {
            get { return _sendAsRaw; }
            set
            {
                _sendAsRaw = value;
                OnPropertyChanged(nameof(SendAsRaw));
            }
        }

        private bool _canSendAsRaw;

        public bool CanSendAsRaw
        {
            get { return _canSendAsRaw; }
            set
            {
                _canSendAsRaw = value;
                OnPropertyChanged(nameof(CanSendAsRaw));
            }
        }

        private bool _parseBeforeSend;

        public bool ParseBeforeSend
        {
            get { return _parseBeforeSend; }
            set
            {
                _parseBeforeSend = value;
                OnPropertyChanged(nameof(ParseBeforeSend));
            }
        }

        private bool _clearAfterSent;

        public bool ClearAfterSent
        {
            get { return _clearAfterSent; }
            set
            {
                _clearAfterSent = value;
                OnPropertyChanged(nameof(ClearAfterSent));
            }
        }

        private bool _ignoreParsingErrors;

        public bool IgnoreParsingErrors
        {
            get { return _ignoreParsingErrors; }
            set
            {
                _ignoreParsingErrors = value;
                OnPropertyChanged(nameof(IgnoreParsingErrors));
            }
        }

        private bool _repeat;

        public bool Repeat
        {
            get { return _repeat; }
            set
            {
                _repeat = value;
                OnPropertyChanged(nameof(Repeat));
                ClearAfterSent = false;
            }
        }

        private int _repeatTimes;

        public int RepeatTimes
        {
            get { return _repeatTimes; }
            set
            {
                _repeatTimes = value;
                OnPropertyChanged(nameof(RepeatTimes));
            }
        }

        private ObservableCollectionEx<VariableViewModel> _variables;

        public ObservableCollectionEx<VariableViewModel> Variables
        {
            get { return _variables; }
            set
            {
                _variables = value;
                OnPropertyChanged(nameof(Variables));
            }
        }

        private string _templatesFilter;

        public string TemplatesFilter
        {
            get { return _templatesFilter; }
            set
            {
                _templatesFilter = value;

                if (_templatesFilter != null)
                {
                    TemplatesView.Filter = o =>
                        o is TemplateViewModel template && template.Name.ToLowerInvariant().Contains(TemplatesFilter.ToLowerInvariant());
                }
                else
                {
                    TemplatesView.Filter = null;
                }
                OnPropertyChanged(nameof(TemplatesView));
            }
        }

        private ObservableCollectionEx<TemplateViewModel> _templates;

        public ObservableCollectionEx<TemplateViewModel> Templates
        {
            get { return _templates; }
            set
            {
                _templates = value;
                OnPropertyChanged(nameof(Templates));

                if (_templates != null)
                {
                    TemplatesView = CollectionViewSource.GetDefaultView(_templates);
                    TemplatesView.GroupDescriptions.Add(new PropertyGroupDescription("Category"));
                    TemplatesView.SortDescriptions.Add(new SortDescription("SortOrder", ListSortDirection.Ascending));

                    OnPropertyChanged(nameof(TemplatesView));
                }
            }
        }

        public ICollectionView TemplatesView { get; private set; }

        private TemplateViewModel _selectedTemplate;

        public TemplateViewModel SelectedTemplate
        {
            get { return _selectedTemplate; }
            set
            {
                _selectedTemplate = value;
                OnPropertyChanged(nameof(SelectedTemplate));

                LoadTemplateCommand.NotifyCanExecuteChanged();
            }
        }

        private ObservableCollectionEx<MacroViewModel> _macros;

        public ObservableCollectionEx<MacroViewModel> Macros
        {
            get { return _macros; }
            set
            {
                _macros = value;
                OnPropertyChanged(nameof(Macros));

                if (_macros != null)
                {
                    MacrosView = CollectionViewSource.GetDefaultView(_macros);
                    MacrosView.GroupDescriptions.Add(new PropertyGroupDescription("Category"));
                    MacrosView.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));

                    OnPropertyChanged(nameof(TemplatesView));
                }
            }
        }

        public ICollectionView MacrosView { get; private set; }

        private MacroViewModel _selectedMacro;

        public MacroViewModel SelectedMacro
        {
            get { return _selectedMacro; }
            set
            {
                _selectedMacro = value;
                OnPropertyChanged(nameof(SelectedMacro));
            }
        }

        private ObservableCollectionEx<ProfileViewModel> _profiles;

        public ObservableCollectionEx<ProfileViewModel> Profiles
        {
            get { return _profiles; }
            set
            {
                _profiles = value;
                OnPropertyChanged(nameof(Profiles));

                if (_profiles != null)
                {
                    ProfilesView = CollectionViewSource.GetDefaultView(_profiles);
                    OnPropertyChanged(nameof(ProfilesView));
                }

            }
        }

        public ICollectionView ProfilesView { get; private set; }

        private ProfileViewModel _selectedProfile;

        public ProfileViewModel SelectedProfile
        {
            get { return _selectedProfile; }
            set
            {
                _selectedProfile = value;
                OnPropertyChanged(nameof(SelectedProfile));

                LoadProfileCommand.NotifyCanExecuteChanged();
                DeleteElementProfileCommand.NotifyCanExecuteChanged();
            }
        }

        private int _selectedProfileIndex = -1;

        public int SelectedProfileIndex
        {
            get { return _selectedProfileIndex; }
            set
            {
                _selectedProfileIndex = value;
                OnPropertyChanged(nameof(SelectedProfileIndex));
            }
        }

        public AsyncRelayCommand OpenTransportCommand { get; private set; }

        private async Task OpenTransportAsync()
        {
            await ExecuteAsync(async () =>
                {
                    AddStatusMessage("Connecting...");

                    var timeoutCancellationToken = _operationTimeout.ToCancellationToken();

                    X509Certificate2 clientCertificate = null;

                    if (!string.IsNullOrWhiteSpace(ClientCertificateThumbprint))
                    {
                        ClientCertificateThumbprint = ClientCertificateThumbprint
                            .Replace(" ", "")
                            .Replace("‎", "");

                        var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);

                        try
                        {
                            store.Open(OpenFlags.ReadOnly);

                            var certificates = store.Certificates.Find(X509FindType.FindByThumbprint, ClientCertificateThumbprint, false);
                            if (certificates.Count > 0)
                            {
                                clientCertificate = certificates[0];

                                var identity = clientCertificate.GetIdentity();

                                if (identity != null)
                                {
                                    var fromVariableViewModel = this.Variables.FirstOrDefault(v => v.Name.Equals("from", StringComparison.OrdinalIgnoreCase));

                                    if (fromVariableViewModel == null)
                                    {
                                        fromVariableViewModel = new VariableViewModel()
                                        {
                                            Name = "from"
                                        };

                                        this.Variables.Add(fromVariableViewModel);
                                    }

                                    fromVariableViewModel.Value = identity.ToString();
                                }
                            }
                            else
                            {
                                AddStatusMessage("The specified certificate was not found", true);
                            }
                        }
                        finally
                        {
                            store.Close();
                        }
                    }

                    if (_hostUri.Scheme == WebSocketTransportListener.UriSchemeWebSocket ||
                        _hostUri.Scheme == WebSocketTransportListener.UriSchemeWebSocketSecure)
                    {
                        Transport = new ClientWebSocketTransport(
                            new EnvelopeSerializer(new DocumentTypeResolver()),
                            this);
                    }
                    else
                    {
                        TcpClient = new TcpClientAdapter(new TcpClient());
                        Transport = new TcpTransport(
                            TcpClient,
                            new EnvelopeSerializer(new DocumentTypeResolver()),
                            _hostUri.Host,
                            clientCertificate,
                            traceWriter: this);
                    }

                    await Transport.OpenAsync(_hostUri, timeoutCancellationToken);

                    _connectionCts = new CancellationTokenSource();

                    var dispatcher = Dispatcher.CurrentDispatcher;

                    _receiveTask = ReceiveAsync(
                        Transport,
                        (e) => ReceiveEnvelopeAsync(e, dispatcher),
                        _connectionCts.Token)
                    .WithCancellation(_connectionCts.Token)
                    .ContinueWith(t =>
                    {
                        IsConnected = false;

                        if (t.Exception != null)
                        {
                            AddStatusMessage(string.Format("Disconnected with errors: {0}", t.Exception.InnerException.Message.RemoveCrLf()), true);
                        }
                        else
                        {
                            AddStatusMessage("Disconnected");
                        }
                    }, TaskScheduler.FromCurrentSynchronizationContext());

                    IsConnected = true;
                    CanSendAsRaw = true;

                    AddStatusMessage("Connected");
                });
        }

        private bool CanOpenTransport()
        {
            return
                !IsBusy &&
                !IsConnected &&
                Uri.TryCreate(_host, UriKind.Absolute, out _hostUri);
        }

        public IAsyncRelayCommand CloseTransportCommand { get; private set; }

        private async Task CloseTransportAsync()
        {
            await ExecuteAsync(async () =>
                {
                    AddStatusMessage("Disconnecting...");

                    var timeoutCancellationToken = _operationTimeout.ToCancellationToken();

                    _connectionCts.Cancel();

                    // Closes the transport
                    await Transport.CloseAsync(timeoutCancellationToken);
                    await _receiveTask.WithCancellation(timeoutCancellationToken);

                    Transport.DisposeIfDisposable();
                    Transport = null;
                });
        }

        private bool CanCloseTransport()
        {
            return
                !IsBusy &&
                IsConnected;
        }

        public RelayCommand IndentCommand { get; private set; }

        private void Indent()
        {
            Execute(() =>
                {
                    InputJson = InputJson.IndentJson();
                });
        }

        private bool CanIndent()
        {
            return !string.IsNullOrWhiteSpace(InputJson);
        }

        public RelayCommand ValidateCommand { get; private set; }

        private void Validate()
        {
            try
            {
                var envelopeViewModel = EnvelopeViewModel.Parse(InputJson);

                AddStatusMessage(string.Format("The input is a valid {0} JSON Envelope", envelopeViewModel.Envelope.GetType().Name));

                var variables = InputJson.GetVariables();

                foreach (var variable in variables)
                {
                    if (!this.Variables.Any(v => v.Name.Equals(variable, StringComparison.OrdinalIgnoreCase)))
                    {
                        var variableViewModel = new VariableViewModel()
                        {
                            Name = variable
                        };

                        this.Variables.Add(variableViewModel);
                    }
                }
            }
            catch (Exception exception)
            {
                AddStatusMessage(exception.Message, true);
            }
        }

        private bool CanValidate()
        {
            return !string.IsNullOrWhiteSpace(InputJson);
        }

        public RelayCommand ParseCommand { get; private set; }

        private void Parse()
        {
            Execute(() =>
                {
                    InputJson = ParseInput(InputJson, Variables);
                });
        }

        private bool CanParse()
        {
            return !string.IsNullOrWhiteSpace(InputJson);
        }

        public IAsyncRelayCommand<string> SendCommand { get; private set; }

        private async Task SendAsync(object parameter)
        {
            var times = 0;

            var repeatCountVariable = Variables.FirstOrDefault(v => v.Name == "repeatCount");
            if (repeatCountVariable == null)
            {
                repeatCountVariable = new VariableViewModel()
                {
                    Name = "repeatCount"
                };
                Variables.Add(repeatCountVariable);
            }

            do
            {
                repeatCountVariable.Value = (times + 1).ToString();

                await ExecuteAsync(async () =>
                {
                    AddStatusMessage("Sending...");

                    var inputJson = parameter.ToString();

                    if (ParseBeforeSend)
                    {
                        inputJson = ParseInput(inputJson, Variables);
                    }

                    var timeoutCancellationToken = _operationTimeout.ToCancellationToken();

                    var envelopeViewModel = new EnvelopeViewModel(false);
                    envelopeViewModel.Json = inputJson;
                    var envelope = envelopeViewModel.Envelope;
                    envelopeViewModel.Direction = DataOperation.Send;

                    if (SendAsRaw)
                    {
                        envelopeViewModel.IsRaw = true;
                        var stream = TcpClient.GetStream();
                        var envelopeBytes = Encoding.UTF8.GetBytes(envelopeViewModel.Json);
                        await stream.WriteAsync(envelopeBytes, 0, envelopeBytes.Length, timeoutCancellationToken);
                    }
                    else
                    {
                        await Transport.SendAsync(envelope, timeoutCancellationToken);
                        envelopeViewModel.IndentJson();
                    }

                    Envelopes.Add(envelopeViewModel);

                    if (ClearAfterSent)
                    {
                        InputJson = string.Empty;
                    }

                    AddStatusMessage(string.Format("{0} envelope sent", envelope.GetType().Name));
                });
            } while (Repeat && RepeatTimes > ++times);
        }

        private bool CanSend(string parameter)
        {
            return
                !IsBusy &&
                IsConnected &&
                !string.IsNullOrWhiteSpace(InputJson);
        }

        public RelayCommand ClearTraceCommand { get; private set; }

        private void ClearTrace()
        {
            this.Envelopes.Clear();
        }

        public RelayCommand LoadTemplateCommand { get; private set; }

        private void LoadTemplate()
        {
            this.InputJson = SelectedTemplate.JsonTemplate.IndentJson();
            this.Validate();
            AddStatusMessage("Template loaded");
        }

        private bool CanLoadTemplate()
        {
            return SelectedTemplate != null;
        }

        public RelayCommand LoadProfileCommand { get; private set; }

        private void LoadProfile()
        {
            if (SelectedProfile == null)
                return;

            Variables = new ObservableCollectionEx<VariableViewModel>();

            foreach (var item in SelectedProfile.JsonValues)
            {
                Variables.Add(new VariableViewModel
                {
                    Name = item.Key,
                    Value = item.Value
                });
            }

            ProfileName = SelectedProfile.Name;
        }

        public RelayCommand SaveProfileCommand { get; private set; }

        private void SaveProfile()
        {
            var variablesDictionary = new Dictionary<string, string>();

            foreach (var item in Variables)
            {
                variablesDictionary.Add(item.Name, item.Value);
            }

            var existingProfile = Profiles.FirstOrDefault(p => p.Name.Equals(ProfileName, StringComparison.InvariantCultureIgnoreCase));

            if (existingProfile != null)
            {
                var existingIndex = Profiles.IndexOf(existingProfile);
                existingProfile.JsonValues = variablesDictionary;

                Profiles[existingIndex] = existingProfile;

                SelectedProfileIndex = existingIndex;
                AddStatusMessage($"Changes applided to profile '{ProfileName}' sucessfully!");

                return;
            }

            Profiles.Add(new ProfileViewModel
            {
                Name = ProfileName,
                JsonValues = variablesDictionary
            });

            SelectedProfileIndex = Profiles.Count - 1;

            AddStatusMessage($"Profile '{ProfileName}' created successfully!");
        }

        public RelayCommand DeleteElementProfileCommand { get; private set; }

        private void DeleteProfile()
        {
            Profiles.Remove(SelectedProfile);
            ProfileName = string.Empty;
        }

        private bool CanDeleteProfile() => SelectedProfile != null;

        private void Execute(Action action)
        {
            IsBusy = true;

            try
            {
                action();
            }
            catch (Exception ex)
            {
                AddStatusMessage(ex.Message.RemoveCrLf());
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExecuteAsync(Func<Task> func)
        {
            IsBusy = true;

            try
            {
                await func();
            }
            catch (Exception ex)
            {
                AddStatusMessage(ex.Message.RemoveCrLf(), true);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private Task _receiveTask;
        private CancellationTokenSource _connectionCts;

        private static async Task ReceiveAsync(ITransport transport, Func<Envelope, Task> processFunc, CancellationToken cancellationToken)
        {
            if (transport == null)
            {
                throw new ArgumentNullException("transport");
            }

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var envelope = await transport.ReceiveAsync(cancellationToken);
                    await processFunc(envelope);
                }
            }
            catch (OperationCanceledException) { }
        }

        public const string HOST_FILE_NAME = "Host.txt";

        private void LoadHost()
        {
            var appDataFileName = FileUtil.GetAppDataFileName(HOST_FILE_NAME);
            if (File.Exists(appDataFileName))
            {
                Host = File.ReadAllText(appDataFileName);
            }
        }

        private void SaveHost()
        {
            if (!string.IsNullOrEmpty(Host))
            {
                var appDataFileName = FileUtil.GetAppDataFileName(HOST_FILE_NAME);
                File.WriteAllText(appDataFileName, Host);
            }
        }

        public const string TEMPLATES_FILE_NAME = "Templates.txt";
        public const char TEMPLATES_FILE_SEPARATOR = '\t';

        private void LoadTemplates()
        {
            foreach (var lineValues in FileUtil.GetFileLines(TEMPLATES_FILE_NAME, TEMPLATES_FILE_SEPARATOR))
            {
                if (lineValues.Length >= 3)
                {
                    var templateViewModel = new TemplateViewModel()
                    {
                        Name = lineValues[0],
                        Category = lineValues[1],
                        JsonTemplate = lineValues[2]
                    };

                    this.Templates.Add(templateViewModel);
                }
            }
        }

        public const string VARIABLES_FILE_NAME = "Variables.txt";
        public const char VARIABLES_FILE_SEPARATOR = '\t';

        public const string CONFIGURATION_FILE_NAME = "Configuration.txt";
        public const char CONFIGURATION_FILE_SEPARATOR = '\t';

        public const string PROFILE_FILE_NAME = "Profiles.json";

        private void LoadVariables()
        {
            foreach (var lineValues in FileUtil.GetFileLines(VARIABLES_FILE_NAME, VARIABLES_FILE_SEPARATOR))
            {
                if (lineValues.Length >= 2)
                {
                    var variableViewModel = new VariableViewModel()
                    {
                        Name = lineValues[0],
                        Value = lineValues[1]
                    };

                    Variables.Add(variableViewModel);
                }
            }
        }

        private void LoadProfiles()
        {
            var content = FileUtil.GetFileContent<ObservableCollectionEx<ProfileViewModel>>(PROFILE_FILE_NAME);

            if (content != null)
                Profiles = content;
        }

        private void LoadConfigurations()
        {
            foreach (var configuration in FileUtil.GetFileLines(CONFIGURATION_FILE_NAME, CONFIGURATION_FILE_SEPARATOR))
            {
                var property = typeof(SessionViewModel).GetProperty(configuration.First());
                property.SetValue(this, Convert.ChangeType(configuration.Last(), property.PropertyType), null);
            }
        }

        private void SaveVariables()
        {
            var lineValues = Variables.Select(v => new[] { v.Name, v.Value }).ToArray();
            FileUtil.SaveFile(lineValues, VARIABLES_FILE_NAME, VARIABLES_FILE_SEPARATOR);
        }

        /// <summary>
        /// Allow save every persistent configuration
        /// </summary>
        private void SaveConfigurations()
        {
            var configurations = new List<string[]>();
            configurations.Add(new string[] { nameof(DarkMode), DarkMode.ToString() });

            FileUtil.SaveFile(configurations, CONFIGURATION_FILE_NAME, CONFIGURATION_FILE_SEPARATOR);
        }

        private string ParseInput(string input, IEnumerable<VariableViewModel> variables)
        {
            var variableValues = variables.ToDictionary(t => t.Name, t => t.Value);

            try
            {
                return input.ReplaceVariables(variableValues);
            }
            catch (ArgumentException) when (IgnoreParsingErrors)
            {
                AddStatusMessage("Some variables could not be parsed", true);
                return input;
            }
        }

        public const string MACROS_FILE_NAME = "Macros.txt";
        public const char MACROS_FILE_SEPARATOR = '\t';

        private void LoadMacros()
        {
            var macroTypes = Assembly
                .GetExecutingAssembly()
                .GetTypes()
                .Where(t => typeof(IMacro).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract && t.GetCustomAttribute<MacroAttribute>() != null)
                .OrderBy(t => t.GetCustomAttribute<MacroAttribute>().Order);

            foreach (var type in macroTypes)
            {
                var macroViewModel = new MacroViewModel()
                {
                    Type = type
                };

                Macros.Add(macroViewModel);
            }

            foreach (var lineValues in FileUtil.GetFileLines(MACROS_FILE_NAME, MACROS_FILE_SEPARATOR))
            {
                if (lineValues.Length >= 2)
                {
                    var macro = Macros.FirstOrDefault(m => m.Name == lineValues[0]);
                    if (macro != null)
                    {
                        macro.IsActive = bool.Parse(lineValues[1]);
                    }
                }
            }
        }

        private void SaveMacros()
        {
            var lineValues = Macros.Select(m => new[] { m.Name, m.IsActive.ToString() }).ToArray();
            FileUtil.SaveFile(lineValues, MACROS_FILE_NAME, MACROS_FILE_SEPARATOR);
        }

        private async Task ReceiveEnvelopeAsync(Envelope envelope, Dispatcher dispatcher)
        {
            var envelopeViewModel = new EnvelopeViewModel
            {
                Envelope = envelope,
                Direction = DataOperation.Receive
            };

            await await dispatcher.InvokeAsync(async () =>
                {
                    Envelopes.Add(envelopeViewModel);

                    foreach (var macro in Macros.Where(m => m.IsActive))
                    {
                        await macro.Macro.ProcessAsync(envelopeViewModel, this);
                    }
                });
        }

        private void AddStatusMessage(string message, bool isError = false)
        {
            StatusMessage = message;

            var statusMessageViewModel = new StatusMessageViewModel()
            {
                Timestamp = DateTimeOffset.Now,
                Message = message,
                IsError = isError
            };

            StatusMessages.Add(statusMessageViewModel);
        }

        public async Task TraceAsync(string data, DataOperation operation)
        {
            var envelopeViewModel = new EnvelopeViewModel(false)
            {
                IsRaw = true,
                Json = data,
                Direction = operation
            };

            await App.Current.Dispatcher.InvokeAsync(() => this.Envelopes.Add(envelopeViewModel));
        }

        public bool IsEnabled
        {
            get { return ShowRawValues; }
        }

        public void SavePreferences()
        {
            if (!UIHelper.IsInDesignMode)
            {
                SaveHost();
                SaveVariables();
                SaveProfiles();
                SaveMacros();
                SaveConfigurations();

            }
        }

        private void SaveProfiles()
        {
            FileUtil.SaveFile(Profiles, PROFILE_FILE_NAME);
        }
    }

    public static class VariablesExtensions
    {
        private static Regex _variablesRegex = new Regex(@"(?<=%)(\w+)", RegexOptions.Compiled);
        private static readonly string _variablePatternFormat = @"\B%{0}\b";
        private static readonly string _guidVariableName = "newGuid";
        private static Regex _guidRegex = new Regex(string.Format(_variablePatternFormat, _guidVariableName));

        public static IEnumerable<string> GetVariables(this string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new ArgumentNullException("input");
            }

            foreach (Match match in _variablesRegex.Matches(input))
            {
                yield return match.Value;
            }
        }

        public static string ReplaceVariables(this string input, Dictionary<string, string> variableValues)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new ArgumentNullException("input");
            }

            if (variableValues == null)
            {
                throw new ArgumentNullException("variableValues");
            }

            input = _guidRegex.Replace(input, m => Guid.NewGuid().ToString());
            var variableNames = input.GetVariables();

            foreach (var variableName in variableNames)
            {
                string variableValue;

                if (!variableValues.TryGetValue(variableName, out variableValue))
                {
                    throw new ArgumentException(string.Format("The variable '{0}' is not present", variableName));
                }

                if (string.IsNullOrWhiteSpace(variableValue))
                {
                    throw new ArgumentException(string.Format("The value of the variable '{0}' is empty", variableName));
                }

                int deepth = 0;

                while (variableValue.StartsWith("%"))
                {
                    var innerVariableName = variableValue.TrimStart('%');

                    if (string.Equals(innerVariableName, _guidVariableName))
                    {
                        variableValue = Guid.NewGuid().ToString();
                        break;
                    }

                    if (!variableValues.TryGetValue(innerVariableName, out variableValue))
                    {
                        throw new ArgumentException(string.Format("The variable '{0}' is not present", innerVariableName));
                    }

                    deepth++;

                    if (deepth > 10)
                    {
                        throw new ArgumentException("Deepth variable limit reached");
                    }
                }

                var variableRegex = new Regex(string.Format(_variablePatternFormat, variableName));
                input = variableRegex.Replace(input, variableValue);
            }

            return input;
        }
    }
}