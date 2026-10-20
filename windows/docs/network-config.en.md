[简体中文](network-config.md) | **English**

# Device Debug Tools · Device network configuration

This module follows the device-management workflow of ZLAN VirCom and independently implements the vendor's published [UDP management-port protocol](https://www.zlmcu.com/download/UDP_port_protocol.pdf). It does not depend on the reference application or vendor DLLs. Management traffic uses UDP 1092, separately from data connections in the TCP / UDP module.

## Workflow

1. Open **网口配置软件** and select the local IPv4 interface connected to the device.
2. Click **搜索局域网设备** for discovery, or enter a device IP and click **按 IP 查询**. Opening the page does not start discovery. If no reply arrives, check the interface, device power, connectivity and UDP 1092 reachability.
3. Select a device and review its identity, original IP and current settings. Use **重读所选设备** to refresh its parameters.
4. Edit its name, IP, subnet mask, gateway, DHCP, operating mode, local/destination ports and destination address, plus baud rate, data bits, parity, stop bits and flow control. Choose transparent transport, Modbus TCP ↔ RTU or RealCom conversion where supported by the device's declared features.
5. Click **检查修改**, review the device and settings, then **确认并写入设备**. Writing restarts the device. Enter its configuration password if required.
6. Success requires reading the same device identity again and verifying its settings. If sending succeeded but verification timed out, rediscover and check the device before retrying.

A device can have a factory IP in a different subnet. Discovery alone does not establish a route to that IP; check network reachability if directed reads or configuration time out.

## Templates and supported formats

JSON templates contain editable settings only, excluding device identity, raw responses, configuration passwords and Wi-Fi secrets. Loading a template does not write it. Writes use a fresh snapshot of the selected device and preserve unrelated authentication, capability flags and extension fields.

Writing currently supports the documented 167-byte parameter format. Other recognized lengths are read-only. Firmware upgrades, P2P, password changes and other vendors' private protocols are not implemented. This module does not modify the computer's own network-interface addresses.

Protocol encoding/decoding, device-identity checks and UDP interaction are verified against a local simulated device. Physical models and firmware still require hardware validation.
