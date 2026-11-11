FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY GilDom.Relay.csproj .
RUN dotnet restore GilDom.Relay.csproj
COPY . .
RUN dotnet publish GilDom.Relay.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER root
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV GILDOM_DATA=/data/relay-data.json
EXPOSE 8080
ENTRYPOINT ["dotnet", "GilDom.Relay.dll"]
