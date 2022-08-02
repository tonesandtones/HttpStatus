# HttpStatus

HttpStatus is a simple web app that returns whatever HTTP status you request. For example, if you want HTTP status code
202 Accepted, send `GET /202`

## Running

Open `HttpStatus.sln` in your IDE and run the app with the HttpSettings profile in `launchSettings.json`.

## Building
To build and run integration tests, use the provided [Nuke](https://nuke.build) bootstrap scripts.

```shell
./build.ps1 {target} [{next_target} ... ]
./build.sh {target} [{next_target} ... ]
```

See the available build targets with `./build.ps1 --help` or `./build.sh --help`

Feel free to use the [Nuke global tool](https://nuke.build/docs/getting-started/installation/) or any other way of
running the Nuke build if you prefer.

### Nuke targets

| Target name     | Purpose                                                                                                       |
|-----------------|---------------------------------------------------------------------------------------------------------------|
| `Clean`           | cleans the output directories                                                                                 |
| `Restore`         | Nuget restore, equivalent to `dotnet restore`                                                                 |
| `Compile`         | Build the main `HttpStatus` solution                                                                          |
| `Test`            | Runs basic unit tests, equivalent to `dotnet test`. Also pass `--cover` to generate dotCover coverage report  |
| `DockerBuild`     | Build the solution as a docker image                                                                          |
| `IntegrationTest` | Run the docker image and fire the integration test suite at it, see `/integration/httpstatusintegrationtests` |
| `DockerPush`      | Tag and push the docker image to `ghcr.io`                                                                    |

As with any Nuke build, you can pass `--plan` to see what targets Nuke would execute.
