FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["HttpStatus/HttpStatus.csproj", "HttpStatus/"]
RUN dotnet restore "HttpStatus/HttpStatus.csproj"
COPY . .
WORKDIR "/src/HttpStatus"
RUN dotnet build "HttpStatus.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "HttpStatus.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "HttpStatus.dll"]
