# SpecOps
### Tactical Scripting

SpecOps is a .NET5 web-based application that allows you to centralize and host your PowerShell scripts for execution by non-technical staff.  The application is written with PowerShell in mind, but could easily be extended to allow other types.  

Scripts are added and configured dynamically through the **scriptsettings.json** configuration file.  The individual scripts are placed in separate files in the **Scripts** folder.  This allows scripts to be added and changed on the fly, as well as allows for the dynamic generation of a GUI for the users with support for all HTML5 input element types (i.e. text, date, number, etc.).  The application leverages the [jQuery Validation library](https://jqueryvalidation.org/) so that validation is also possible by specifying HTML5-based attributes such as required, pattern, etc.

The application leverages [Bootstrap4](https://getbootstrap.com/docs/4.0/getting-started/introduction/) and the [AdminLTE](https://adminlte.io/) theme for styling with all plugins available.  

Script output is returned in realtime via [SignalR](https://dotnet.microsoft.com/apps/aspnet/signalr).  [Serilog](https://serilog.net/) is set up for logging to a file, but can easily be extended to log to a database, elasticsearch, etc.

_Please note this application is still considered **beta**._
