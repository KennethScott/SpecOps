# SpecOps
### Tactical Scripting

<img src="SpecOps/wwwroot/dist/img/banner-SpecOps.PNG" width="800">
<br/><br/>

SpecOps is a .NET7 web application that allows you to centralize and host your **PowerShell 7** scripts for execution by anyone, particularly non-technical staff.  The application is written with PowerShell in mind, but could easily be extended to allow other types.  The application is intended to be used in a Windows-based Intranet environment.

Scripts are added and configured dynamically through the **scriptsettings.json** configuration file.  Scripts can be added and changed on the fly, and the configuration specified in the json file allows for the dynamic generation of a GUI for users to input parameter values via rudimentary support for [HTML5 input element types](https://developer.mozilla.org/en-US/docs/Web/HTML/Element/input) (i.e. text, date, number, etc.) and dropdowns via [HTML select elements](https://developer.mozilla.org/en-US/docs/Web/HTML/Element/select).  The application leverages the [jQuery Validation library](https://jqueryvalidation.org/) so that basic validation is also possible by specifying HTML5-based attributes such as [required](https://developer.mozilla.org/en-US/docs/Web/HTML/Attributes/required), [pattern](https://developer.mozilla.org/en-US/docs/Web/HTML/Attributes/pattern), etc.

SpecOps now also contains a realtime PowerShell terminal to aid in script development or to simply execute a quick command.  The terminal is made possible by [jQueryTerminal](https://terminal.jcubic.pl/), and syntax highlighting of input is done via [PrismJS](https://prismjs.com/).  The terminal is restricted to members of the Admin policy. 

The application leverages [Bootstrap4](https://getbootstrap.com/docs/4.0/getting-started/introduction/) and the [AdminLTE](https://adminlte.io/) theme for styling with all plugins available.  

Script output is returned in realtime via [SignalR](https://dotnet.microsoft.com/apps/aspnet/signalr).  [Serilog](https://serilog.net/) is set up for logging to a file, but can easily be extended to log to a database, elasticsearch, etc.

#### Security

Security is based around the concept of **Security Policies** for different types of users (i.e. Admin, HelpDesk, L2-Support, etc.).  Scripts themselves are grouped into **Categories**.  Security Policies are defined in the **appsettings.json** file, and specify the Categories of scripts allowed for users in that policy as well as the Active Directory groups that are members of that policy.  To access the site, you must be a member of an AD group included in one of the policies.  The Script Runner page automatically determines the appropriate script Categories available and only shows the relevant scripts.

```json
    "SecurityPolicies": [
      {
        "Name": "Admin",
        "Groups": [ "mydomain\\abc", "mydomain\\def" ],
        "CategoryIds": [ "Admin" ]
      },
      {
        "Name": "HelpDesk",
        "Groups": [ "mydomain\\uvw", "mydomain\\xyz" ],
        "CategoryIds": [ "MyApp1", "MyApp2" ]
      }
    ]
```

The application also works off a domain - simply use your computer name\login instead of domain\login.


#### Script Configuration

Scripts are configured in **scriptsettings.json**.

1) **CategoryId** allows for grouping of related scripts and can be any meaningful value (Admin, HelpDesk, MyApplication, etc.)
2) **Id** is the script's unique identifier and should be unique across all scripts (regardless of Category).  A GUID is recommended.
3) **PathAndFilename** may be relevant to the application's folder (i.e. ./Scripts/Demo.ps1) or could be any fully qualified path to which the AppPoolIdentity has access.
4) **Name** is the user-friendly (short) name of the script that will be shown in the dropdown.
5) **Summary** is a brief description of the script and will be shown when selected in the dropdown.
6) **InputParms** is an array of Input Parameter objects for each parameter to be passed into the script.
    * **Name** should match the parameter name expected in the script
    * **Description** is a brief user-friendly description of the parameter
    * **Placeholder** optional text to be shown inside the input element (if relevant)    
    * **Type** specifies the desired input element type to use for the parameter and is based on the [HTML5 input element types](https://developer.mozilla.org/en-US/docs/Web/HTML/Element/input).  Proper dropdowns are also supported by using type=**select** in conjunction with the **Options** parameter.  
    * **Value** can be used to set the initial/default value for the input element 
    * **Min**, **Max**, **Step** are integer values used in combination with Type=**range** to facilitate a slider
    * **List** is an array of values used in combination with Type=**text** to facilitate a combobox (range list)
    * **Options** is a dictionary of key/value pairs used in combination with Type=**select** to facilitate a dropdown
    * **Required** with a value of **true** may be used to cause the parameter to be required
    * **Pattern** may be used to apply a desired regex pattern for validation
    * **Logging** is an optional boolean used to disable logging of the parameter value in the Serilog sinks (defaults to true)
    * **System Generated Input Parameters** are automatically available to all scripts.  They could be used for anything, but most commonly for additional custom logging purposes.  See /Scripts/Demo.ps1 for an example of usage.
      * **SpecOpsCurrentUser** is the domain and userid of the user running the script
      * **SpecOpsCurrentUserIP** is the IP address of the user running the script
      * **SpecOpsScriptId** is the script id of the script being executed
7) Custom Runspace pools are also supported 
    * **ExecutionPolicy** allows for specifying the desired [Microsoft.PowerShell.ExecutionPolicy](https://docs.microsoft.com/en-us/dotnet/api/microsoft.powershell.executionpolicy?view=powershellsdk-7.0.0) string name such as Unrestricted, Bypass, etc.
    * **Min** is an integer value and specifies the minimum number of runspaces to allocate to the pool
    * **Max** is an integer value and specifies the maximum number of runspaces to allocate to the pool.  Recommended at n+1 where n=number of processors.
    * **Modules** allows for an array of strings for specifying PowerShell modules to load into the pool

```json
"ScriptSettings": [
    {
      "CategoryId": "Admin",
      "Id": "7CF9D423-5CB7-432B-A3E9-618340ADF5A7",
      "PathAndFilename": "./Scripts/Demo.ps1",
      "Name": "Demo",
      "Summary": "This is a brief summary of what the demo script does....",
      "InputParms": [
        {
          "Name": "StrParam1",
          "Type": "text",
          "Description": "Input 3-letter String Parameter",
          "Placeholder": "Enter text here...",
          "Required": "true",
          "Pattern": "[A-Za-z]{3}"
        },
        {
          "Name": "DropdownParam1",
          "Type": "select",
          "Description": "Dropdown Select Parameter",
          "Options": {
            "": "Select a value...",
            "abc": "ABC Text To Display",
            "def": "DEF Text To Display",
            "ghi": "GHI Text To Display"
          },
          "Required": "true"
        },
      ],
      "Runspace": {
        "ExecutionPolicy":  "Unrestricted",
        "Min": 1,
        "Max": 2,
        "Modules": [
          "PSDiagnostics"
        ]
      }
    }
  }
]
```

<br/>

<img src="SpecOps/wwwroot/dist/img/script-page-example.png" width="1000">

<br/><br/>

#### Script Writing

Writing to the different output streams in the PowerShell script will trigger different output levels in the log.  The desired css class can be specified for each level via the **appsettings.json** file as to facilitate the desired color coding of the log text.  Each log record is also timestamped.

| PowerShell Write               | Output Level |
| :---                           | :---         |
| `Write-Output`                | Data         |
| `Write-Host`                  | Info         |
| `Write-Information`          | Info         |
| `Write-Warning`               | Warning      |
| `Write-Debug`                 | Debug        |
| `Write-Verbose`               | Verbose      |
| `Write-Progress`              | Progress     |
| `Write-Error` (or Exceptions) | Error        |

Two additional output types not specific to the scripts are:
* **System** used by SpecOps itself to indicate things like script execution has started, ended, etc.
* **Unknown** is essentially a catch-all in case output is produced and somehow not mapped to one of the above.

Example showing each Output Level being mapped to the desired css class:

```json
    "OutputLevels": [
      { "Name": "Data", "CssClass": "so-data" },
      { "Name": "Error", "CssClass": "so-error" },
      { "Name": "Warning", "CssClass": "so-warning" },
      { "Name": "Info", "CssClass": "so-info" },
      { "Name": "Progress", "CssClass": "so-progress" },
      { "Name": "Verbose", "CssClass": "so-verbose" },
      { "Name": "Debug", "CssClass": "so-debug" },
      { "Name": "System", "CssClass": "so-system" },
      { "Name": "Unknown", "CssClass": "so-unknown" }
    ]
```

<img src="SpecOps/wwwroot/dist/img/log-example.PNG" width="1000">
<br/><br/>

#### Terminal
SpecOps now includes a realtime PowerShell terminal to aid in script development or simply executing a quick command.  The terminal leverages the [jQueryTerminal library](https://terminal.jcubic.pl/).  
Access to the terminal is restricted to members of the Admin policy.  
Script output is styled the same as the output table on the Script Runner page.  Input is also syntax highlighted via [PrismJS](https://prismjs.com/).  

Any code entered is executed and logged in exactly the same way as the predefined scripts are - meaning they will run under the security context of the App Pool identity.

Please be very careful in allowing access to the Terminal as it could be very dangerous in the hands of unskilled users.
<br/><br/>

<img src="SpecOps/wwwroot/dist/img/terminal-page-example.PNG" width="1000">
<br/><br/>

#### Logging
Serilog is used to log detailed information about the application, the scripts being executed, etc.  It is currently configured to log to text files in the **/logs** folder.  

System-generated input parameters are also available in every script automatically and could be used for additional custom logging purposes.  See the input parameters section for details.  
<br/>


#### Miscellaneous

The Script Runner page supports routing parameters for the CategoryId and the ScriptId.  This allows you to construct links directly to the desired category, and optionally, script.  
`.../Pages/Scripts/{CategoryId}/{ScriptId}`

<br/><br/>

#### Credits

Thank you to [@keithbabinec](https://github.com/keithbabinec) for his work with hosted runspaces in his [PowerShellHostedRunspaceStarterKits](https://github.com/keithbabinec/PowerShellHostedRunspaceStarterkits) project.

<br/>
