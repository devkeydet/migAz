﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using MigAz.Azure.Arm;
using MigAz.Azure.Asm;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using MigAz.Core.ArmTemplate;
using System.Windows.Forms;

namespace MigAz.Azure
{
    public class AzureRetriever
    {
        private AzureContext _AzureContext;
        private object _lockObject = new object();
        private AzureSubscription _AzureSubscription = null;
        private List<AzureSubscription> _AzureSubscriptions;

        public delegate void OnRestResultHandler(Guid requestGuid, string url, string response);
        public event OnRestResultHandler OnRestResult;

        // ASM Object Cache (Subscription Context Specific)
        private List<Asm.VirtualNetwork> _VirtualNetworks;
        private List<Asm.StorageAccount> _StorageAccounts;
        private List<CloudService> _CloudServices;
        private List<ReservedIP> _AsmReservedIPs;

        // ARM Object Cache (Subscription Context Specific)
        private List<Arm.Location> _ArmLocations;
        private List<AzureTenant> _ArmTenants;
        private List<AzureSubscription> _ArmSubscriptions;
        private List<ResourceGroup> _ArmResourceGroups;
        private List<Arm.VirtualNetwork> _ArmVirtualNetworks;
        private List<Arm.StorageAccount> _ArmStorageAccounts;
        private List<Asm.AvailabilitySet> _AsmAvailabilitySets;
        private List<Arm.AvailabilitySet> _ArmAvailabilitySets;
        private List<Arm.VirtualMachine> _ArmVirtualMachines;
        private List<Arm.ManagedDisk> _ArmManagedDisks;

        private Dictionary<string, XmlDocument> _asmXmlDocumentCache = new Dictionary<string, XmlDocument>();
        private Dictionary<string, JObject> _armJsonDocumentCache = new Dictionary<string, JObject>();

        private AzureRetriever() { }

        public AzureRetriever(AzureContext azureContext)
        {
            _AzureContext = azureContext;
        }

        public void ClearCache()
        {
            _asmXmlDocumentCache = new Dictionary<string, XmlDocument>();
            _armJsonDocumentCache = new Dictionary<string, JObject>();
            _ArmLocations = null;
            _ArmTenants = null;
            _ArmSubscriptions = null;
            _ArmResourceGroups = null;
            _ArmVirtualNetworks = null;
            _ArmStorageAccounts = null;
            _ArmAvailabilitySets = null;
            _AsmAvailabilitySets = null;
            _ArmManagedDisks = null;
            _VirtualNetworks = null;
            _StorageAccounts = null;
            _CloudServices = null;
        }

        protected XmlDocument RemoveXmlns(String xml)
        {
            if (!xml.StartsWith("<"))
            {
                xml = $"<data>{xml}</data>";
            }
            XDocument d = XDocument.Parse(xml);
            d.Root.DescendantsAndSelf().Attributes().Where(x => x.IsNamespaceDeclaration).Remove();

            foreach (var elem in d.Descendants())
                elem.Name = elem.Name.LocalName;

            var xmlDocument = new XmlDocument();
            xmlDocument.Load(d.CreateReader());

            return xmlDocument;
        }

        private void writeRetreiverResultToLog(Guid requestGuid, string method, string url, string xml)
        {
            lock (_lockObject)
            {
                string logfilepath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\MigAz\\MigAz-XML-" + string.Format("{0:yyyyMMdd}", DateTime.Now) + ".log";
                string text = DateTime.Now.ToString() + "  " + requestGuid.ToString() + "  " + url + Environment.NewLine;
                File.AppendAllText(logfilepath, text);
                File.AppendAllText(logfilepath, xml + Environment.NewLine);
                File.AppendAllText(logfilepath, Environment.NewLine);
            }

            _AzureContext.LogProvider.WriteLog(method, requestGuid.ToString() + " Received REST Response");
        }


        public Dictionary<string, string> GetVMLBMapping(string cloudservicename, XmlNodeList roles)
        {
            Dictionary<string, string> aslbnamemapping = new Dictionary<string, string>();
            Dictionary<string, string> aslbnamemapping2 = new Dictionary<string, string>();

            foreach (XmlNode role in roles)
            {
                string availabilitysetname = String.Empty;
                if (role.SelectNodes("AvailabilitySetName").Count > 0)
                {
                    availabilitysetname = role.SelectSingleNode("AvailabilitySetName").InnerText;
                }

                if (!aslbnamemapping.ContainsKey(availabilitysetname))
                {
                    aslbnamemapping.Add(availabilitysetname, String.Empty);
                }
            }

            if (aslbnamemapping.Count == 1)
            {
                foreach (KeyValuePair<string, string> keyvaluepair in aslbnamemapping)
                {
                    aslbnamemapping2.Add(keyvaluepair.Key, cloudservicename);
                }
            }
            else
            {
                int increment = 1;
                foreach (KeyValuePair<string, string> keyvaluepair in aslbnamemapping)
                {
                    aslbnamemapping2.Add(keyvaluepair.Key, cloudservicename + "-LB" + increment.ToString());
                    increment += 1;
                }
            }

            Dictionary<string, string> vmlbmapping = new Dictionary<string, string>();

            foreach (XmlNode role in roles)
            {
                string virtualmachinename = role.SelectSingleNode("RoleName").InnerText;
                string availabilitysetname = String.Empty;
                if (role.SelectNodes("AvailabilitySetName").Count > 0)
                {
                    availabilitysetname = role.SelectSingleNode("AvailabilitySetName").InnerText;
                }
                string loadbalancername = aslbnamemapping2[availabilitysetname];

                vmlbmapping.Add(virtualmachinename, loadbalancername);
            }

            return vmlbmapping;
        }

        internal async Task SetSubscriptionContext(AzureSubscription azureSubscription)
        {
            ClearCache();
            _AzureSubscription = azureSubscription;
            await this._AzureContext.TokenProvider.GetToken(azureSubscription);
        }

        public async Task<List<AzureSubscription>> GetSubscriptions()
        {
            if (_AzureSubscriptions != null)
                return _AzureSubscriptions;

            _AzureSubscriptions = new List<AzureSubscription>();
            XmlDocument subscriptionsXml = await this.GetAzureAsmResources("Subscriptions", null);
            foreach (XmlNode subscriptionXml in subscriptionsXml.SelectNodes("//Subscription"))
            {
                AzureSubscription azureSubscription = new AzureSubscription(subscriptionXml, this._AzureContext.AzureEnvironment);
                _AzureSubscriptions.Add(azureSubscription);
            }

            return _AzureSubscriptions;
        }

        #region ASM Methods

        private async Task<XmlDocument> GetAzureAsmResources(string resourceType, Hashtable info)
        {
            _AzureContext.LogProvider.WriteLog("GetAzuereAsmResources", "Start");

            Guid requestGuid = Guid.NewGuid();
            _AzureContext.LogProvider.WriteLog("GetAzureASMResources", requestGuid.ToString() + " Start REST Request");

            string url = null;
            switch (resourceType)
            {
                case "Subscriptions":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + "subscriptions";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Subscriptions...");
                    break;
                case "VirtualNetworks":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/networking/virtualnetwork";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Virtual Networks for Subscription ID : " + _AzureSubscription.SubscriptionId + "...");
                    break;
                case "ClientRootCertificates":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/networking/" + info["virtualnetworkname"] + "/gateway/clientrootcertificates";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Client Root Certificates for Virtual Network : " + info["virtualnetworkname"] + "...");
                    break;
                case "ClientRootCertificate":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/networking/" + info["virtualnetworkname"] + "/gateway/clientrootcertificates/" + info["thumbprint"];
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting certificate data for certificate : " + info["thumbprint"] + "...");
                    break;
                case "NetworkSecurityGroup":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/networking/networksecuritygroups/" + info["name"] + "?detaillevel=Full";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Network Security Group : " + info["name"] + "...");
                    break;
                case "NetworkSecurityGroups":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/networking/networksecuritygroups";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Network Security Groups");
                    break;
                case "RouteTable":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/networking/routetables/" + info["name"] + "?detailLevel=full";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Route Table : " + info["routetablename"] + "...");
                    break;
                case "NSGSubnet":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/networking/virtualnetwork/" + info["virtualnetworkname"] + "/subnets/" + info["subnetname"] + "/networksecuritygroups";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting NSG for subnet " + info["subnetname"] + "...");
                    break;
                case "VirtualNetworkGateway":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/networking/" + info["virtualnetworkname"] + "/gateway";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Virtual Network Gateway : " + info["virtualnetworkname"] + "...");
                    break;
                case "VirtualNetworkGatewaySharedKey":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/networking/" + info["virtualnetworkname"] + "/gateway/connection/" + info["localnetworksitename"] + "/sharedkey";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Virtual Network Gateway Shared Key: " + info["localnetworksitename"] + "...");
                    break;
                case "StorageAccounts":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/storageservices";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Storage Accounts for Subscription ID : " + _AzureSubscription.SubscriptionId + "...");
                    break;
                case "StorageAccount":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/storageservices/" + info["name"];
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Storage Account '" + info["name"] + " ' for Subscription ID : " + _AzureSubscription.SubscriptionId + "...");
                    break;
                case "StorageAccountKeys":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/storageservices/" + info["name"] + "/keys";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Storage Account '" + info["name"] + "' Keys.");
                    break;
                case "CloudServices":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/hostedservices";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Cloud Services for Subscription ID : " + _AzureSubscription.SubscriptionId + "...");
                    break;
                case "CloudService":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/hostedservices/" + info["name"] + "?embed-detail=true";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Virtual Machines for Cloud Service : " + info["name"] + "...");
                    break;
                case "VirtualMachine":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/hostedservices/" + info["cloudservicename"] + "/deployments/" + info["deploymentname"] + "/roles/" + info["virtualmachinename"];
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Virtual Machine '" + info["virtualmachinename"] + "' for Cloud Service '" + info["virtualmachinename"] + "'");
                    break;
                case "VMImages":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/images";
                    break;
                case "ReservedIPs":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/services/networking/reservedips";
                    break;
                case "AffinityGroup":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/affinitygroups/" + info["affinitygroupname"];
                    break;
                case "Locations":
                    url = AzureServiceUrls.GetASMServiceManagementUrl(this._AzureContext.AzureEnvironment) + _AzureSubscription.SubscriptionId + "/locations";
                    break;
            }

            _AzureContext.LogProvider.WriteLog("GetAzureASMResources", requestGuid.ToString() + " Url: " + url);

            if (_asmXmlDocumentCache.ContainsKey(url))
            {
                _AzureContext.LogProvider.WriteLog("GetAzureASMResources", requestGuid.ToString() + " Using Cached Response");
                _AzureContext.LogProvider.WriteLog("GetAzureASMResources", requestGuid.ToString() + " End REST Request");
                return _asmXmlDocumentCache[url];
            }

            HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(url);

            string authorizationHeader = "Bearer " + _AzureContext.TokenProvider.AuthenticationResult.AccessToken;
            _AzureContext.LogProvider.WriteLog("GetAzureASMResources", requestGuid.ToString() + " " + "Adding authorization header: " + authorizationHeader);
            request.Headers.Add(HttpRequestHeader.Authorization, authorizationHeader);

            request.Headers.Add("x-ms-version", "2015-04-01");
            request.Method = "GET";

            XmlDocument xmlDocument = null;
            string httpWebResponseValue = String.Empty;
            try
            {
                _AzureContext.LogProvider.WriteLog("GetAzureASMResources", requestGuid.ToString() + " " + request.Method + " " + url);

                // Retry Guidlines for 500 series with Backoff Timer - https://msdn.microsoft.com/en-us/library/azure/jj878112.aspx  https://msdn.microsoft.com/en-us/library/azure/gg185909.aspx
                HttpWebResponse response = null;
                Int32 retrySeconds = 1;
                bool boolRetryGetResponse = true;
                while (boolRetryGetResponse)
                {
                    try
                    {
                        response = (HttpWebResponse)await request.GetResponseAsync();
                        boolRetryGetResponse = false;
                    }
                    catch (WebException webException)
                    {
                        _AzureContext.LogProvider.WriteLog("GetAzureASMResources", requestGuid.ToString() + " EXCEPTION " + webException.Message);

                        HttpWebResponse exceptionResponse = (HttpWebResponse) webException.Response;

                        if ((int)exceptionResponse.StatusCode >= 500 && (int)exceptionResponse.StatusCode <= 599)
                        {
                            DateTime sleepUntil = DateTime.Now.AddSeconds(retrySeconds);
                            string sleepMessage = "Sleeping for " + retrySeconds.ToString() + " second(s) (until " + sleepUntil.ToString() + ") before web request retry.";

                            _AzureContext.LogProvider.WriteLog("GetAzureASMResources", requestGuid.ToString() + " " + sleepMessage);
                            _AzureContext.StatusProvider.UpdateStatus(sleepMessage);
                            while (DateTime.Now < sleepUntil)
                            { 
                                Application.DoEvents();
                            }
                            retrySeconds = retrySeconds * 2;

                            _AzureContext.LogProvider.WriteLog("GetAzureASMResources", requestGuid.ToString() + " Initiating retry of Web Request.");
                            _AzureContext.StatusProvider.UpdateStatus("Initiating retry of Web Request.");
                        }
                        else
                            throw webException;
                    }
                }

                _AzureContext.LogProvider.WriteLog("GetAzureASMResources", requestGuid.ToString() + " Status Code " + response.StatusCode);

                httpWebResponseValue = new StreamReader(response.GetResponseStream()).ReadToEnd();
                writeRetreiverResultToLog(requestGuid, "GetAzureAsmResources", url, httpWebResponseValue);
                if (httpWebResponseValue != String.Empty)
                {
                    xmlDocument = RemoveXmlns(httpWebResponseValue);

                    if (!_asmXmlDocumentCache.ContainsKey(url))
                        _asmXmlDocumentCache.Add(url, xmlDocument);
                }
            }
            catch (Exception exception)
            {
                _AzureContext.LogProvider.WriteLog("GetAzureASMResources", requestGuid.ToString() + " EXCEPTION " + exception.Message);
                throw exception;
            }

            _AzureContext.LogProvider.WriteLog("GetAzureASMResources", requestGuid.ToString() + " End REST Request");

            OnRestResult?.Invoke(requestGuid, url, httpWebResponseValue);

            return xmlDocument;
        }

        public async virtual Task<List<Asm.Location>> GetAzureASMLocations()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureASMLocations", "Start");

            XmlNode locationsXml = await this.GetAzureAsmResources("Locations", null);
            List<Asm.Location> azureLocations = new List<Asm.Location>();
            foreach (XmlNode locationXml in locationsXml.SelectNodes("/Locations/Location"))
            {
                azureLocations.Add(new Asm.Location(_AzureContext, locationXml));
            }

            return azureLocations;
        }

        public async virtual Task<List<ReservedIP>> GetAzureAsmReservedIPs()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmReservedIPs", "Start");

            if (_AsmReservedIPs != null)
                return _AsmReservedIPs;

            _AsmReservedIPs = new List<ReservedIP>();
            XmlDocument reservedIPsXml = await this.GetAzureAsmResources("ReservedIPs", null);
            foreach (XmlNode reservedIPXml in reservedIPsXml.SelectNodes("/ReservedIPs/ReservedIP"))
            {
                _AsmReservedIPs.Add(new ReservedIP(_AzureContext, reservedIPXml));
            }

            return _AsmReservedIPs;
        }

        public async virtual Task<List<Asm.StorageAccount>> GetAzureAsmStorageAccounts()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmStorageAccounts", "Start");

            if (_StorageAccounts != null)
                return _StorageAccounts;

            _StorageAccounts = new List<Asm.StorageAccount>();
            XmlDocument storageAccountsXml = await this.GetAzureAsmResources("StorageAccounts", null);
            foreach (XmlNode storageAccountXml in storageAccountsXml.SelectNodes("//StorageService"))
            {
                Asm.StorageAccount asmStorageAccount = new Asm.StorageAccount(_AzureContext, storageAccountXml);
                _StorageAccounts.Add(asmStorageAccount);
            }

            _AzureContext.StatusProvider.UpdateStatus("BUSY: Loading Storage Account Keys");
            List<Task> storageAccountKeyTasks = new List<Task>();
            foreach (Asm.StorageAccount asmStorageAccount in _StorageAccounts)
            {
                storageAccountKeyTasks.Add(asmStorageAccount.LoadStorageAccountKeysAsynch());
            }
            await Task.WhenAll(storageAccountKeyTasks);

            return _StorageAccounts;
        }

        public async virtual Task<Asm.StorageAccount> GetAzureAsmStorageAccount(string storageAccountName)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmStorageAccount", "Start");

            foreach (Asm.StorageAccount asmStorageAccount in await this.GetAzureAsmStorageAccounts())
            {
                if (asmStorageAccount.Name == storageAccountName)
                    return asmStorageAccount;
            }

            return null;
        }

        public async virtual Task<List<Asm.VirtualNetwork>> GetAzureAsmVirtualNetworks()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmVirtualNetworks", "Start");

            if (_VirtualNetworks != null)
                return _VirtualNetworks;

            _VirtualNetworks = new List<Asm.VirtualNetwork>();
            foreach (XmlNode virtualnetworksite in (await this.GetAzureAsmResources("VirtualNetworks", null)).SelectNodes("//VirtualNetworkSite"))
            {
                Asm.VirtualNetwork asmVirtualNetwork = new Asm.VirtualNetwork(_AzureContext, virtualnetworksite);
                await asmVirtualNetwork.InitializeChildrenAsync();
                _VirtualNetworks.Add(asmVirtualNetwork);
            }
            return _VirtualNetworks;
        }

        public async Task<Asm.VirtualNetwork> GetAzureAsmVirtualNetwork(string virtualNetworkName)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmVirtualNetwork", "Start");

            foreach (Asm.VirtualNetwork asmVirtualNetwork in await this.GetAzureAsmVirtualNetworks())
            {
                if (asmVirtualNetwork.Name == virtualNetworkName)
                {
                    return asmVirtualNetwork;
                }
            }

            return null;
        }

        internal async Task<AffinityGroup> GetAzureAsmAffinityGroup(string affinityGroupName)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmAffinityGroup", "Start");

            Hashtable affinitygroupinfo = new Hashtable();
            affinitygroupinfo.Add("affinitygroupname", affinityGroupName);

            XmlNode affinityGroupXml = await this.GetAzureAsmResources("AffinityGroup", affinitygroupinfo);
            AffinityGroup asmAffinityGroup = new AffinityGroup(_AzureContext, affinityGroupXml.SelectSingleNode("AffinityGroup"));
            return asmAffinityGroup;
        }

        public async virtual Task<Asm.NetworkSecurityGroup> GetAzureAsmNetworkSecurityGroup(string networkSecurityGroupName)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmNetworkSecurityGroup", "Start");

            Hashtable networkSecurityGroupInfo = new Hashtable();
            networkSecurityGroupInfo.Add("name", networkSecurityGroupName);

            XmlNode networkSecurityGroupXml = await this.GetAzureAsmResources("NetworkSecurityGroup", networkSecurityGroupInfo);
            Asm.NetworkSecurityGroup asmNetworkSecurityGroup = new Asm.NetworkSecurityGroup(_AzureContext, networkSecurityGroupXml.SelectSingleNode("NetworkSecurityGroup"));
            return asmNetworkSecurityGroup;
        }

        public async virtual Task<List<Asm.NetworkSecurityGroup>> GetAzureAsmNetworkSecurityGroups()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmNetworkSecurityGroups", "Start");

            List<Asm.NetworkSecurityGroup> networkSecurityGroups = new List<Asm.NetworkSecurityGroup>();

            XmlDocument x = await this.GetAzureAsmResources("NetworkSecurityGroups", null);
            foreach (XmlNode networkSecurityGroupNode in (await this.GetAzureAsmResources("NetworkSecurityGroups", null)).SelectNodes("//NetworkSecurityGroup"))
            {
                Asm.NetworkSecurityGroup asmNetworkSecurityGroup = new Asm.NetworkSecurityGroup(_AzureContext, networkSecurityGroupNode);
                networkSecurityGroups.Add(asmNetworkSecurityGroup);
            }

            return networkSecurityGroups;
        }

        public async virtual Task<Asm.RouteTable> GetAzureAsmRouteTable(string routeTableName)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmRouteTable", "Start");

            Hashtable info = new Hashtable();
            info.Add("name", routeTableName);
            XmlDocument routeTableXml = await this.GetAzureAsmResources("RouteTable", info);
            return new Asm.RouteTable(_AzureContext, routeTableXml);
        }

        internal async Task<Asm.VirtualMachine> GetAzureAsmVirtualMachine(CloudService asmCloudService, string virtualMachineName)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmVirtualMachine", "Start");

            Hashtable vmDetails = await this.GetVMDetails(asmCloudService.ServiceName, virtualMachineName);
            XmlDocument virtualMachineXml = await this.GetAzureAsmResources("VirtualMachine", vmDetails);
            Asm.VirtualMachine asmVirtualMachine = new Asm.VirtualMachine(this._AzureContext, asmCloudService, this._AzureContext.SettingsProvider, virtualMachineXml, vmDetails);
            await asmVirtualMachine.InitializeChildren();

            return asmVirtualMachine;
        }

        private async Task<Hashtable> GetVMDetails(string cloudServiceName, string virtualMachineName)
        {
            _AzureContext.LogProvider.WriteLog("GetVMDetails", "Start");

            Hashtable cloudserviceinfo = new Hashtable();
            cloudserviceinfo.Add("name", cloudServiceName);

            Hashtable virtualmachineinfo = new Hashtable();
            virtualmachineinfo.Add("cloudservicename", cloudServiceName);
            virtualmachineinfo.Add("virtualmachinename", virtualMachineName);

            XmlDocument hostedservice = await GetAzureAsmResources("CloudService", cloudserviceinfo);
            if (hostedservice.SelectNodes("//Deployments/Deployment").Count > 0)
            {
                if (hostedservice.SelectNodes("//Deployments/Deployment")[0].SelectNodes("RoleList/Role")[0].SelectNodes("RoleType").Count > 0)
                {
                    if (hostedservice.SelectNodes("//Deployments/Deployment")[0].SelectNodes("RoleList/Role")[0].SelectSingleNode("RoleType").InnerText == "PersistentVMRole")
                    {
                        if (hostedservice.SelectNodes("//Deployments/Deployment")[0].SelectSingleNode("VirtualNetworkName") != null)
                        {
                            virtualmachineinfo.Add("virtualnetworkname", hostedservice.SelectNodes("//Deployments/Deployment")[0].SelectSingleNode("VirtualNetworkName").InnerText);
                        }
                        else
                        {
                            virtualmachineinfo.Add("virtualnetworkname", String.Empty);
                        }

                        virtualmachineinfo.Add("deploymentname", hostedservice.SelectNodes("//Deployments/Deployment")[0].SelectSingleNode("Name").InnerText);

                        XmlNodeList roles = hostedservice.SelectNodes("//Deployments/Deployment")[0].SelectNodes("RoleList/Role");
                        // GetVMLBMapping is necessary because a Cloud Service can have multiple availability sets
                        // On ARM, a load balancer can only be attached to 1 availability set
                        // Because of this, if multiple availability sets exist, we are breaking the cloud service in multiple load balancers
                        //     to respect all availability sets
                        Dictionary<string, string> vmlbmapping = GetVMLBMapping(cloudServiceName, roles);
                        foreach (XmlNode role in roles)
                        {
                            string currentVM = role.SelectSingleNode("RoleName").InnerText;
                            if (currentVM == virtualMachineName)
                            {
                                virtualmachineinfo.Add("loadbalancername", vmlbmapping[virtualMachineName]);
                                return virtualmachineinfo;
                            }
                        }
                    }
                }
            }
            throw new InvalidOperationException("Requested VM could not be found");
        }

        public async virtual Task<List<CloudService>> GetAzureAsmCloudServices()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmCloudServices", "Start");

            if (_CloudServices != null)
                return _CloudServices;

            XmlDocument cloudServicesXml = await this.GetAzureAsmResources("CloudServices", null);
            _CloudServices = new List<CloudService>();
            foreach (XmlNode cloudServiceXml in cloudServicesXml.SelectNodes("//HostedService"))
            {
                CloudService tempCloudService = new CloudService(_AzureContext, cloudServiceXml);

                Hashtable cloudServiceInfo = new Hashtable();
                cloudServiceInfo.Add("name", tempCloudService.ServiceName);
                XmlDocument cloudServiceDetailXml = await this.GetAzureAsmResources("CloudService", cloudServiceInfo);
                CloudService asmCloudService = new CloudService(_AzureContext, cloudServiceDetailXml);

                _CloudServices.Add(asmCloudService);
            }

            List<Task> cloudServiceVMTasks = new List<Task>();
            foreach (CloudService asmCloudService in _CloudServices)
            {
                cloudServiceVMTasks.Add(asmCloudService.LoadChildrenAsync());
            }

            await Task.WhenAll(cloudServiceVMTasks);

            return _CloudServices;
        }

        public async virtual Task<CloudService> GetAzureAsmCloudService(string cloudServiceName)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmCloudService", "Start");

            foreach (CloudService asmCloudService in await this.GetAzureAsmCloudServices())
            {
                if (asmCloudService.ServiceName == cloudServiceName)
                    return asmCloudService;
            }

            return null;
        }

        public async Task<StorageAccountKeys> GetAzureAsmStorageAccountKeys(string storageAccountName)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmStorageAccountKeys", "Start");

            Hashtable storageAccountInfo = new Hashtable();
            storageAccountInfo.Add("name", storageAccountName);

            XmlDocument storageAccountKeysXml = await this.GetAzureAsmResources("StorageAccountKeys", storageAccountInfo);
            return new StorageAccountKeys(_AzureContext, storageAccountKeysXml);
        }

        public async virtual Task<List<ClientRootCertificate>> GetAzureAsmClientRootCertificates(Asm.VirtualNetwork asmVirtualNetwork)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmClientRootCertificates", "Start");

            Hashtable virtualNetworkInfo = new Hashtable();
            virtualNetworkInfo.Add("virtualnetworkname", asmVirtualNetwork.Name);
            XmlDocument clientRootCertificatesXml = await this.GetAzureAsmResources("ClientRootCertificates", virtualNetworkInfo);

            List<ClientRootCertificate> asmClientRootCertificates = new List<ClientRootCertificate>();
            foreach (XmlNode clientRootCertificateXml in clientRootCertificatesXml.SelectNodes("//ClientRootCertificate"))
            {
                ClientRootCertificate asmClientRootCertificate = new ClientRootCertificate(_AzureContext, asmVirtualNetwork, clientRootCertificateXml);
                await asmClientRootCertificate.InitializeChildrenAsync();
                asmClientRootCertificates.Add(asmClientRootCertificate);
            }

            return asmClientRootCertificates;
        }

        public async Task<XmlDocument> GetAzureAsmClientRootCertificateData(Asm.VirtualNetwork asmVirtualNetwork, string certificateThumbprint)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmClientRootCertificateData", "Start");

            Hashtable certificateInfo = new Hashtable();
            certificateInfo.Add("virtualnetworkname", asmVirtualNetwork.Name);
            certificateInfo.Add("thumbprint", certificateThumbprint);
            XmlDocument clientRootCertificateXml = await this.GetAzureAsmResources("ClientRootCertificate", certificateInfo);
            return clientRootCertificateXml;
        }

        public async virtual Task<Asm.VirtualNetworkGateway> GetAzureAsmVirtualNetworkGateway(Asm.VirtualNetwork asmVirtualNetwork)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmVirtualNetworkGateway", "Start");

            Hashtable virtualNetworkGatewayInfo = new Hashtable();
            virtualNetworkGatewayInfo.Add("virtualnetworkname", asmVirtualNetwork.Name);
            virtualNetworkGatewayInfo.Add("localnetworksitename", String.Empty);

            XmlDocument gatewayXml = await this.GetAzureAsmResources("VirtualNetworkGateway", virtualNetworkGatewayInfo);
            return new Asm.VirtualNetworkGateway(_AzureContext, asmVirtualNetwork, gatewayXml);
        }

        public async virtual Task<string> GetAzureAsmVirtualNetworkSharedKey(string virtualNetworkName, string localNetworkSiteName)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureAsmVirtualNetworkSharedKey", "Start");

            try
            {
                Hashtable virtualNetworkGatewayInfo = new Hashtable();
                virtualNetworkGatewayInfo.Add("virtualnetworkname", virtualNetworkName);
                virtualNetworkGatewayInfo.Add("localnetworksitename", localNetworkSiteName);

                XmlDocument connectionShareKeyXml = await this.GetAzureAsmResources("VirtualNetworkGatewaySharedKey", virtualNetworkGatewayInfo);
                if (connectionShareKeyXml.SelectSingleNode("//Value") == null)
                    return String.Empty;

                return connectionShareKeyXml.SelectSingleNode("//Value").InnerText;
            }
            catch (Exception exc)
            {
                _AzureContext.LogProvider.WriteLog("GetAzureAsmVirtualNetworkSharedKey", "Exception: " + exc.Message + exc.StackTrace);
                return String.Empty;
            }
        }

        #endregion

        #region ARM Methods

        internal Asm.AvailabilitySet GetAzureASMAvailabilitySet(Asm.VirtualMachine asmVirtualMachine)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureASMAvailabilitySet", "Start");

            if (_AsmAvailabilitySets == null)
                _AsmAvailabilitySets = new List<Asm.AvailabilitySet>();

            foreach (Asm.AvailabilitySet armAvailabilitySet in _AsmAvailabilitySets)
            {
                if (armAvailabilitySet.name == asmVirtualMachine.GetDefaultAvailabilitySetName())
                    return armAvailabilitySet;
            }

            Asm.AvailabilitySet newArmAvailabilitySet = new Asm.AvailabilitySet(this._AzureContext, asmVirtualMachine);
            _AsmAvailabilitySets.Add(newArmAvailabilitySet);

            return newArmAvailabilitySet;
        }

        private async Task<JObject> GetAzureARMResources(string resourceType, Hashtable info)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureARMResources", "Start");

            string methodType = "GET";
            string url = null;
            bool useCached = true;
            Guid requestGuid = Guid.NewGuid();

            _AzureContext.LogProvider.WriteLog("GetAzureARMResources", requestGuid.ToString() + " Start REST Request");

            if (_AzureContext.TokenProvider == null || _AzureContext.TokenProvider.AuthenticationResult == null)
                throw new ArgumentNullException("TokenProvider Context or AuthenticationResult Context is null.  Unable to call Azure API without AuthenticationResult.");

            AuthenticationResult authenticationResult = _AzureContext.TokenProvider.AuthenticationResult;

            switch (resourceType)
            {
                case "Tenants":
                    url = AzureServiceUrls.GetARMServiceManagementUrl(this._AzureContext.AzureEnvironment) + "tenants?api-version=2015-01-01";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Tenants...");
                    break;
                case "Domains": // todo, move to a graph class?
                    url = AzureServiceUrls.GetGraphApiUrl(this._AzureContext.AzureEnvironment) + "myorganization/domains?api-version=1.6";
                    authenticationResult = await _AzureContext.TokenProvider.GetGraphToken(this._AzureContext.AzureEnvironment, info["tenantId"].ToString());
                    useCached = false;
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Tenant Domain details from Graph...");
                    break;
                case "Subscriptions":
                    url = AzureServiceUrls.GetARMServiceManagementUrl(this._AzureContext.AzureEnvironment) + "subscriptions?api-version=2015-01-01";

                    if (info != null && info["tenantId"] != null)
                    {
                        authenticationResult = await _AzureContext.TokenProvider.GetAzureToken(this._AzureContext.AzureEnvironment, info["tenantId"].ToString());
                        useCached = false;
                    }

                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting Subscriptions...");
                    break;
                case "ResourceGroups":
                    // https://docs.microsoft.com/en-us/rest/api/resources/resourcegroups#ResourceGroups_List
                    url = AzureServiceUrls.GetARMServiceManagementUrl(this._AzureContext.AzureEnvironment) + "subscriptions/" + _AzureSubscription.SubscriptionId + "/resourcegroups?api-version=2016-09-01";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting ARM Resource Groups...");
                    break;
                case "Locations":
                    // https://docs.microsoft.com/en-us/rest/api/resources/subscriptions#Subscriptions_ListLocations
                    url = AzureServiceUrls.GetARMServiceManagementUrl(this._AzureContext.AzureEnvironment) + "subscriptions/" + _AzureSubscription.SubscriptionId + ArmConst.Locations + "?api-version=2016-06-01";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting ARM Azure Locations for Subscription ID : " + _AzureSubscription.SubscriptionId + "...");
                    break;
                case "AvailabilitySets":
                    // https://docs.microsoft.com/en-us/rest/api/compute/availabilitysets/availabilitysets-list-subscription
                    url = AzureServiceUrls.GetARMServiceManagementUrl(this._AzureContext.AzureEnvironment) + "subscriptions/" + _AzureSubscription.SubscriptionId + ArmConst.ProviderAvailabilitySets + "?api-version=2017-03-30";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting ARM Azure Compute Availability Sets for Subscription ID : " + _AzureSubscription.SubscriptionId + "...");
                    break;
                case "VirtualNetworks":
                    // https://msdn.microsoft.com/en-us/library/azure/mt163557.aspx
                    // https://docs.microsoft.com/en-us/rest/api/network/list-virtual-networks-within-a-subscription
                    url = AzureServiceUrls.GetARMServiceManagementUrl(this._AzureContext.AzureEnvironment) + "subscriptions/" + _AzureSubscription.SubscriptionId + ArmConst.ProviderVirtualNetwork + "?api-version=2016-12-01";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting ARM Virtual Networks for Subscription ID : " + _AzureSubscription.SubscriptionId + "...");
                    break;
                case "StorageAccounts":
                    // https://docs.microsoft.com/en-us/rest/api/storagerp/storageaccounts#StorageAccounts_List
                    url = AzureServiceUrls.GetARMServiceManagementUrl(this._AzureContext.AzureEnvironment) + "subscriptions/" + _AzureSubscription.SubscriptionId + ArmConst.ProviderStorageAccounts + "?api-version=2016-01-01";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting ARM Storage Accounts for Subscription ID : " + _AzureSubscription.SubscriptionId + "...");
                    break;
                case "StorageAccountKeys":
                    // https://docs.microsoft.com/en-us/rest/api/storagerp/storageaccounts#StorageAccounts_ListKeys
                    methodType = "POST";
                    url = AzureServiceUrls.GetARMServiceManagementUrl(this._AzureContext.AzureEnvironment) + "subscriptions/" + _AzureSubscription.SubscriptionId + "/resourceGroups/" + info["ResourceGroupName"] + ArmConst.ProviderStorageAccounts + info["StorageAccountName"] + "/listKeys?api-version=2016-01-01";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting ARM Storage Account Key for Subscription ID : " + _AzureSubscription.SubscriptionId + " / Storage Account: " + info["StorageAccountName"] + " ...");
                    break;
                case "VirtualMachines":
                    // https://docs.microsoft.com/en-us/rest/api/compute/virtualmachines/virtualmachines-list-subscription
                    url = AzureServiceUrls.GetARMServiceManagementUrl(this._AzureContext.AzureEnvironment) + "subscriptions/" + _AzureSubscription.SubscriptionId + ArmConst.ProviderVirtualMachines + "?api-version=2016-03-30";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting ARM Virtual Machines for Subscription ID : " + _AzureSubscription.SubscriptionId + "...");
                    break;
                case "ManagedDisks":
                    // https://docs.microsoft.com/en-us/rest/api/manageddisks/disks/disks-list-by-subscription
                    url = AzureServiceUrls.GetARMServiceManagementUrl(this._AzureContext.AzureEnvironment) + "subscriptions/" + _AzureSubscription.SubscriptionId + ArmConst.ProviderVirtualMachines + "?api-version=2016-04-30-preview";
                    _AzureContext.StatusProvider.UpdateStatus("BUSY: Getting ARM Managed Disks for Subscription ID : " + _AzureSubscription.SubscriptionId + "...");
                    break;
            }

            _AzureContext.LogProvider.WriteLog("GetAzureARMResources", requestGuid.ToString() + " Url: " + url);

            if (useCached && _armJsonDocumentCache.ContainsKey(url))
            {
                _AzureContext.LogProvider.WriteLog("GetAzureARMResources", requestGuid.ToString() + " Using Cached Response");
                _AzureContext.LogProvider.WriteLog("GetAzureARMResources", requestGuid.ToString() + " End REST Request");
                return _armJsonDocumentCache[url];
            }

            HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(url);
            string authorizationHeader = "Bearer " + authenticationResult.AccessToken;
            _AzureContext.LogProvider.WriteLog("GetAzureARMResources", "Adding authorization header - " + authorizationHeader);
            request.Headers.Add(HttpRequestHeader.Authorization, authorizationHeader);

            request.ContentType = "application/json";
            request.Method = methodType;

            if (request.Method == "POST")
                request.ContentLength = 0;

            string webRequesetResult = String.Empty;
            JObject webRequestResultJson = null;
            try
            {
                _AzureContext.LogProvider.WriteLog("GetAzureARMResources", requestGuid.ToString() + " " + request.Method + " " + url);

                // Retry Guidlines for 500 series with Backoff Timer - https://msdn.microsoft.com/en-us/library/azure/jj878112.aspx  https://msdn.microsoft.com/en-us/library/azure/gg185909.aspx
                HttpWebResponse response = null;
                Int32 retrySeconds = 1;
                bool boolRetryGetResponse = true;
                while (boolRetryGetResponse)
                {
                    try
                    {
                        response = (HttpWebResponse)await request.GetResponseAsync();
                        boolRetryGetResponse = false;
                    }
                    catch (WebException webException)
                    {
                        _AzureContext.LogProvider.WriteLog("GetAzureARMResources", requestGuid.ToString() + " EXCEPTION " + webException.Message);

                        HttpWebResponse exceptionResponse = (HttpWebResponse)webException.Response;

                        if ((int)exceptionResponse.StatusCode >= 500 && (int)exceptionResponse.StatusCode <= 599)
                        {
                            DateTime sleepUntil = DateTime.Now.AddSeconds(retrySeconds);
                            string sleepMessage = "Sleeping for " + retrySeconds.ToString() + " second(s) (until " + sleepUntil.ToString() + ") before web request retry.";

                            _AzureContext.LogProvider.WriteLog("GetAzureARMResources", requestGuid.ToString() + " " + sleepMessage);
                            _AzureContext.StatusProvider.UpdateStatus(sleepMessage);
                            while (DateTime.Now < sleepUntil)
                            {
                                Application.DoEvents();
                            }
                            retrySeconds = retrySeconds * 2;

                            _AzureContext.LogProvider.WriteLog("GetAzureARMResources", requestGuid.ToString() + " Initiating retry of Web Request.");
                            _AzureContext.StatusProvider.UpdateStatus("Initiating retry of Web Request.");
                        }
                        else
                            throw webException;
                    }
                }

                webRequesetResult = new StreamReader(response.GetResponseStream()).ReadToEnd();

                writeRetreiverResultToLog(requestGuid, "GetAzureARMResources", url, webRequesetResult);
                _AzureContext.LogProvider.WriteLog("GetAzureARMResources", requestGuid.ToString() + "  Status Code " + response.StatusCode);

                if (webRequesetResult != String.Empty)
                {
                    webRequestResultJson = JObject.Parse(webRequesetResult);

                    if (useCached && !_armJsonDocumentCache.ContainsKey(url))
                        _armJsonDocumentCache.Add(url, webRequestResultJson);
                }
            }
            catch (Exception exception)
            {
                _AzureContext.LogProvider.WriteLog("GetAzureARMResources", requestGuid.ToString() + "  EXCEPTION " + exception.Message);
                throw exception;
            }

            _AzureContext.LogProvider.WriteLog("GetAzureARMResources", requestGuid.ToString() + " End REST Request");

            OnRestResult?.Invoke(requestGuid, url, webRequesetResult);

            return webRequestResultJson;
        }

        public async Task<List<AzureTenant>> GetAzureARMTenants()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureARMTenants", "Start");

            if (_ArmTenants != null)
                return _ArmTenants;

            JObject tenantsJson = await this.GetAzureARMResources("Tenants", null);

            var tenants = from tenant in tenantsJson["value"]
                                select tenant;

            _ArmTenants = new List<AzureTenant>();

            foreach (JObject tenantJson in tenants)
            {
                AzureTenant azureTenant = new AzureTenant(tenantJson, _AzureContext);
                await azureTenant.InitializeChildren();
                _ArmTenants.Add(azureTenant);
            }

            return _ArmTenants;
        }

        public async Task<List<AzureDomain>> GetAzureARMDomains(AzureTenant azureTenant)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureARMDomains", "Start");

            Hashtable info = new Hashtable();
            info.Add("tenantId", azureTenant.TenantId);

            JObject domainsJson = await this.GetAzureARMResources("Domains", info);

            var domains = from domain in domainsJson["value"]
                          select domain;

            List<AzureDomain> armTenantDomains = new List<AzureDomain>();

            foreach (JObject domainJson in domains)
            {
                AzureDomain azureDomain = new AzureDomain(domainJson, _AzureContext);
                armTenantDomains.Add(azureDomain);
            }

            return armTenantDomains;
        }

        public async Task<List<AzureSubscription>> GetAzureARMSubscriptions()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureARMSubscriptions", "Start");

            if (_ArmSubscriptions != null)
                return _ArmSubscriptions;

            JObject subscriptionsJson = await this.GetAzureARMResources("Subscriptions", null);

            var subscriptions = from subscription in subscriptionsJson["value"]
                                select subscription;

            _ArmSubscriptions = new List<AzureSubscription>();

            foreach (JObject azureSubscriptionJson in subscriptions)
            {
                AzureSubscription azureSubscription = new AzureSubscription(azureSubscriptionJson, null, _AzureContext.AzureEnvironment);
                _ArmSubscriptions.Add(azureSubscription);
            }

            return _ArmSubscriptions;
        }

        public async Task<List<AzureSubscription>> GetAzureARMSubscriptions(AzureTenant azureTenant)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureARMSubscriptions", "Start");

            Hashtable info = new Hashtable();
            info.Add("tenantId", azureTenant.TenantId);

            JObject subscriptionsJson = await this.GetAzureARMResources("Subscriptions", info);

            var subscriptions = from subscription in subscriptionsJson["value"]
                                select subscription;

            List<AzureSubscription> tenantSubscriptions = new List<AzureSubscription>();

            foreach (JObject azureSubscriptionJson in subscriptions)
            {
                AzureSubscription azureSubscription = new AzureSubscription(azureSubscriptionJson, azureTenant, _AzureContext.AzureEnvironment);
                tenantSubscriptions.Add(azureSubscription);
            }

            return tenantSubscriptions;
        }

        public async Task<List<ResourceGroup>> GetAzureARMResourceGroups()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureARMResourceGroups", "Start");

            if (_ArmResourceGroups != null)
                return _ArmResourceGroups;

            JObject resourceGroupsJson = await this.GetAzureARMResources("ResourceGroups", null);

            var resourceGroups = from resourceGroup in resourceGroupsJson["value"]
                                select resourceGroup;

            _ArmResourceGroups = new List<ResourceGroup>();

            foreach (JObject resourceGroupJson in resourceGroups)
            {
                ResourceGroup azureSubscription = new ResourceGroup(resourceGroupJson, _AzureContext.AzureEnvironment);
                _ArmResourceGroups.Add(azureSubscription);
            }

            return _ArmResourceGroups;
        }

        public async virtual Task<Arm.VirtualNetwork> GetAzureARMVirtualNetwork(string virtualNetworkName)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureARMVirtualNetwork", "Start");

            foreach (Arm.VirtualNetwork armVirtualNetwork in await GetAzureARMVirtualNetworks())
            {
                if (armVirtualNetwork.Name == virtualNetworkName)
                    return armVirtualNetwork;
            }

            return null;
        }

        public async virtual Task<List<Arm.VirtualNetwork>> GetAzureARMVirtualNetworks()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureARMVirtualNetworks", "Start");

            if (_ArmVirtualNetworks != null)
                return _ArmVirtualNetworks;

            JObject virtualNetworksJson = await this.GetAzureARMResources("VirtualNetworks", null);

            var virtualNetworks = from vnet in virtualNetworksJson["value"]
                                  select vnet;

            _ArmVirtualNetworks = new List<Arm.VirtualNetwork>();

            foreach (var virtualNetwork in virtualNetworks)
            {
                Arm.VirtualNetwork armVirtualNetwork = new Arm.VirtualNetwork(virtualNetwork);
                _ArmVirtualNetworks.Add(armVirtualNetwork);
            }

            return _ArmVirtualNetworks;
        }

        public async virtual Task<List<Arm.ManagedDisk>> GetAzureARMManagedDisks()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureARMManagedDisks", "Start");

            if (_ArmManagedDisks != null)
                return _ArmManagedDisks;

            JObject managedDisksJson = await this.GetAzureARMResources("ManagedDisks", null);

            var managedDisks = from managedDisk in managedDisksJson["value"]
                                  select managedDisk;

            _ArmManagedDisks = new List<Arm.ManagedDisk>();

            foreach (var managedDisk in managedDisks)
            {
                Arm.ManagedDisk armManagedDisk = new Arm.ManagedDisk(managedDisk);
                _ArmManagedDisks.Add(armManagedDisk);
            }

            return _ArmManagedDisks;
        }
        public async virtual Task<List<Arm.StorageAccount>> GetAzureARMStorageAccounts()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureARMStorageAccounts", "Start");

            if (_ArmStorageAccounts != null)
                return _ArmStorageAccounts;

            JObject storageAccountsJson = await this.GetAzureARMResources("StorageAccounts", null);

            var storageAccounts = from storage in storageAccountsJson["value"]
                                  select storage;

            _ArmStorageAccounts = new List<Arm.StorageAccount>();

            foreach (var storageAccount in storageAccounts)
            {
                Arm.StorageAccount armStorageAccount = new Arm.StorageAccount(_AzureContext, storageAccount);
                await this.GetAzureARMStorageAccountKeys(armStorageAccount);

                _ArmStorageAccounts.Add(armStorageAccount);
            }

            return _ArmStorageAccounts;
        }


        public async virtual Task<List<Arm.Location>> GetAzureARMLocations()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureARMLocations", "Start");

            if (_ArmLocations != null)
                return _ArmLocations;

            JObject locationsJson = await this.GetAzureARMResources("Locations", null);

            var locations = from location in locationsJson["value"]
                                  select location;

            _ArmLocations = new List<Arm.Location>();

            foreach (var location in locations)
            {
                Arm.Location armLocation = new Arm.Location(_AzureContext, location);
                _ArmLocations.Add(armLocation);
            }

            _ArmLocations = _ArmLocations.OrderBy(x => x.DisplayName).ToList();

            return _ArmLocations;
        }

        public async Task<List<Arm.VirtualMachine>> GetAzureArmVirtualMachines()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureArmVirtualMachines", "Start");

            if (_ArmVirtualMachines != null)
                return _ArmVirtualMachines;

            JObject virtualMachineJson = await this.GetAzureARMResources("VirtualMachines", null);

            var virtualMachines = from virtualMachine in virtualMachineJson["value"]
                            select virtualMachine;

            _ArmVirtualMachines = new List<Arm.VirtualMachine>();

            foreach (var virtualMachine in virtualMachines)
            {
                Arm.VirtualMachine armVirtualMachine = new Arm.VirtualMachine(virtualMachine);
                await armVirtualMachine.InitializeChildrenAsync(this._AzureContext);
                _ArmVirtualMachines.Add(armVirtualMachine);
            }

            return _ArmVirtualMachines;
        }

        internal async Task GetAzureARMStorageAccountKeys(Arm.StorageAccount armStorageAccount)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureARMStorageAccountKeys", "Start");

            Hashtable storageAccountKeyInfo = new Hashtable();
            storageAccountKeyInfo.Add("ResourceGroupName", armStorageAccount.ResourceGroup);
            storageAccountKeyInfo.Add("StorageAccountName", armStorageAccount.Name);

            JObject storageAccountKeysJson = await this.GetAzureARMResources("StorageAccountKeys", storageAccountKeyInfo);

            var storageAccountKeys = from keys in storageAccountKeysJson["keys"]
                                  select keys;

            armStorageAccount.Keys.Clear();
            foreach (var storageAccountKey in storageAccountKeys)
            {
                StorageAccountKey armStorageAccountKey = new StorageAccountKey(storageAccountKey);
                armStorageAccount.Keys.Add(armStorageAccountKey);
            }

            return;
        }

        public async Task<List<Arm.AvailabilitySet>> GetAzureARMAvailabilitySets()
        {
            _AzureContext.LogProvider.WriteLog("GetAzureARMAvailabilitySets", "Start");

            if (_ArmAvailabilitySets != null)
                return _ArmAvailabilitySets;

            JObject availabilitySetJson = await this.GetAzureARMResources("AvailabilitySets", null);

            var availabilitySets = from availabilitySet in availabilitySetJson["value"]
                                   select availabilitySet;

            _ArmAvailabilitySets = new List<Arm.AvailabilitySet>();

            foreach (var availabilitySet in availabilitySets)
            {
                Arm.AvailabilitySet armAvailabilitySet = new Arm.AvailabilitySet(availabilitySet);
                _ArmAvailabilitySets.Add(armAvailabilitySet);
            }

            return _ArmAvailabilitySets;
        }

        public async Task<Arm.AvailabilitySet> GetAzureARMAvailabilitySet(string availabilitySetId)
        {
            _AzureContext.LogProvider.WriteLog("GetAzureARMAvailabilitySet", "Start");

            foreach (Arm.AvailabilitySet availabilitySet in await this.GetAzureARMAvailabilitySets())
            {
                if (availabilitySet.Id == availabilitySetId)
                    return availabilitySet;
            }

            return null;
        }

        #endregion

    }
}
