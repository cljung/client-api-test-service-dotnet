@rem FawltyTowers2CampusPass
@set vcType=%1
@if "%vcType%"=="" set vcType=FawltyTowers2CampusPass
rem echo %vcType%
dotnet run AppSettings:ActiveCredentialType=%vcType%