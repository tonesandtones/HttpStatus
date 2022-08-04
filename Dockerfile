FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["src/HttpStatus/HttpStatus.csproj", "HttpStatus/"]
RUN dotnet restore "HttpStatus/HttpStatus.csproj"
COPY src .
WORKDIR "/src/HttpStatus"
RUN dotnet build "HttpStatus.csproj" -c Release -o /app/build

WORKDIR /tests
COPY ["tests/HttpStatusTests/HttpStatusTests.csproj", "HttpStatusTests/"]
RUN dotnet restore "HttpStatusTests/HttpStatusTests.csproj"
COPY tests .
WORKDIR "/tests/HttpStatusTests"
RUN Logging__LogLevel__HttpLoggingMiddlewareOverride=Warning dotnet test "HttpStatusTests.csproj"

FROM build AS publish
WORKDIR "/src/HttpStatus"
RUN dotnet publish "HttpStatus.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "HttpStatus.dll"]
