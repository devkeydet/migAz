﻿using MigAz.Core.Interface;
using System;
using System.Xml;

namespace MigAz.Azure.Asm
{
    public class NetworkInterface
    {
        #region Variables

        private AzureContext _AzureContext;
        private VirtualMachine _AsmVirtualMachine;
        private XmlNode _XmlNode;
        private String _TargetName;

        #endregion

        #region Constructors

        private NetworkInterface() { }

        public NetworkInterface(AzureContext azureContext, VirtualMachine asmVirtualMachine, ISettingsProvider settingsProvider, XmlNode xmlNode)
        {
            _AzureContext = azureContext;
            _AsmVirtualMachine = asmVirtualMachine;
            _XmlNode = xmlNode;

            this.TargetName = _AsmVirtualMachine.RoleName;
        }

        #endregion

        #region Properties

        public VirtualMachine Parent
        {
            get { return _AsmVirtualMachine; }
        }

        public string TargetName
        {
            get { return _TargetName; }
            set { _TargetName = value.Trim(); }
        }
        public string SubnetName
        {
            get { return _XmlNode.SelectSingleNode("IPConfigurations/IPConfiguration/SubnetName").InnerText; }
        }

        public string StaticVirtualNetworkIPAddress
        {
            get
            {
                if (_XmlNode.SelectSingleNode("IPConfigurations/IPConfiguration/StaticVirtualNetworkIPAddress") == null)
                    return String.Empty;

                return _XmlNode.SelectSingleNode("IPConfigurations/IPConfiguration/StaticVirtualNetworkIPAddress").InnerText;
            }
        }

        public string Name
        {
            get { return _XmlNode.SelectSingleNode("Name").InnerText; }
        }

        public bool EnableIPForwarding
        {
            get { return _XmlNode.SelectNodes("IPForwarding").Count > 0; }
        }

        public string GetFinalTargetName()
        {
            return this.TargetName + this._AzureContext.SettingsProvider.NetworkInterfaceCardSuffix;
        }
        #endregion
    }
}
