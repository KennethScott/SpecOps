# SpecOps
### Tactical Scripting

SpecOps is a .NET5 web-based application that allows you to centralize and host your PowerShell scripts for execution by anyone, particularly non-technical staff.  The application is written with PowerShell in mind, but could easily be extended to allow other types.  *The application is intended to be used in a Windows-based Intranet environment.*

Scripts are added and configured dynamically through the **scriptsettings.json** configuration file.  The individual scripts are placed in separate files in the **Scripts** folder.  This allows scripts to be added and changed on the fly, as well as allows for the dynamic generation of a GUI for the users with rudimentary support for [HTML5 input element types](https://developer.mozilla.org/en-US/docs/Web/HTML/Element/input) (i.e. text, date, number, etc.).  The application leverages the [jQuery Validation library](https://jqueryvalidation.org/) so that validation is also possible by specifying HTML5-based attributes such as required, pattern, etc.

Security is based around the construct of **Users** and **Admins**.  Scripts are grouped into **Categories**, and any Category that begins with "Admin" are automatically restricted to members of the Admin group.  Everything else is considered open to members in the Users group.  You must be a member of one or the other to access the site.  The Script Runner page automatically determines the group membership and only shows the relevant scripts.

The Users and Admins groups are configured via **AppSettings.AdminGroups** and **AppSettings.UserGroups** in **appsettings.json**.  Both allow for an array of AD group names.

The application leverages [Bootstrap4](https://getbootstrap.com/docs/4.0/getting-started/introduction/) and the [AdminLTE](https://adminlte.io/) theme for styling with all plugins available.  

Script output is returned in realtime via [SignalR](https://dotnet.microsoft.com/apps/aspnet/signalr).  [Serilog](https://serilog.net/) is set up for logging to a file, but can easily be extended to log to a database, elasticsearch, etc.

Scripts are configured in **scriptsettings.json**.
1) The script Id should be unique across all scripts, therefore a GUID is recommended.
2) Input Parameters are based on the [HTML5 input element types](https://developer.mozilla.org/en-US/docs/Web/HTML/Element/input) 
* **Name** should match the parameter name expected in the script
* **Min**, **Max**, **Step** are used in combination with Type=**range** to facilitate a slider
* **List** is used in combination with Type=**text** to facilitate a dropdown
* **Required** with a value of **true** causes the parameter to be required
* **Pattern** is used to apply a regex pattern for validation

Writing to the different output types in the PowerShell script code will trigger different output types in the output log, and those are mapped to different Bootstrap color classes.

| PowerShell Write            | Output Type |
| :---                        | :---        |
| Write-Output                | Data        |
| Write-Host                  | Info        |
| Write-Information           | Info        |
| Write-Warning               | Warning     |
| Write-Debug                 | Debug       |
| Write-Verbose               | Verbose     |
| Write-Progress              | Progress    |
| Write-Error (or Exceptions) | Error       |


_Please note this application is still considered **beta**._
