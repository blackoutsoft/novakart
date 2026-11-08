FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY NovaKart.WebApi/NovaKart.WebApi.csproj NovaKart.WebApi/
COPY NovaKart.Application/NovaKart.Application.csproj NovaKart.Application/
COPY NovaKart.Domain/NovaKart.Domain.csproj NovaKart.Domain/
COPY NovaKart.Infraestructure/NovaKart.Infraestructure.csproj NovaKart.Infraestructure/
RUN dotnet restore NovaKart.WebApi/NovaKart.WebApi.csproj
COPY . .
RUN dotnet publish NovaKart.WebApi/NovaKart.WebApi.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "NovaKart.WebApi.dll"]
