using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HttpStatusTests;

public class HttpStatusTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _application;
    private readonly HttpClient _client;

    public HttpStatusTests()
    {
        _application = new WebApplicationFactory<Program>();
        _client = _application.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, HandleCookies = false
            });
    }

    public void Dispose()
    {
        _application.Dispose();
        _client.Dispose();
    }

    [Theory]
    [MemberData(nameof(ValidHttpStatusCodes))] 
//'description' is not used, but makes test cases easier to read in test reports.
#pragma warning disable xUnit1026
    public async Task GoodStatusCodeRequestsReturnExpectedStatusCode(string description, int statusCode)
#pragma warning restore xUnit1026
    {
        (await _client.GetAsync($"{statusCode}")).StatusCode
            .Should()
            .Be((HttpStatusCode)statusCode);
    }

    public static IEnumerable<object[]> ValidHttpStatusCodes()
    {
        return Enumerable.Range(100, 900).Select(x => new object[] { $"{x:000}", x });
    }
}