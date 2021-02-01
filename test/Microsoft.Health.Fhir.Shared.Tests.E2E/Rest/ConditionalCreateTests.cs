﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Microsoft.Health.Fhir.Client;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Microsoft.Health.Fhir.Tests.E2E.Common;
using Microsoft.Health.Fhir.Web;
using Microsoft.Health.Test.Utilities;
using Xunit;
using Xunit.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Tests.E2E.Rest
{
    [HttpIntegrationFixtureArgumentSets(DataStore.All, Format.All)]
    [Trait(Traits.Category, Categories.ConditionalCreate)]
    public class ConditionalCreateTests : IClassFixture<HttpIntegrationTestFixture<Startup>>
    {
        private readonly ITestOutputHelper _logger;
        private readonly TestFhirClient _client;

        public ConditionalCreateTests(HttpIntegrationTestFixture<Startup> fixture, ITestOutputHelper logger)
        {
            _logger = logger;
            _client = fixture.TestFhirClient;
        }

        [Fact]
        [Trait(Traits.Priority, Priority.One)]
        public async Task GivenAResource_WhenCreatingConditionallyWithNoIdAndNoExisting_TheServerShouldReturnTheResourceSuccessfully()
        {
            var observation = Samples.GetDefaultObservation().ToPoco<Observation>();
            observation.Id = null;

            using FhirResponse<Observation> updateResponse = await _client.CreateAsync(
                observation,
                $"identifier={Guid.NewGuid().ToString()}");

            Assert.Equal(HttpStatusCode.Created, updateResponse.StatusCode);

            Observation updatedResource = updateResponse.Resource;

            Assert.NotNull(updatedResource);
            Assert.NotNull(updatedResource.Id);
        }

        [Fact]
        [Trait(Traits.Priority, Priority.One)]
        public async Task GivenAResourceWithNoId_WhenCreatingConditionallyWithOneMatch_TheServerShouldReturnOK()
        {
            var observation = Samples.GetDefaultObservation().ToPoco<Observation>();
            var identifier = Guid.NewGuid().ToString();

            observation.Identifier.Add(new Identifier("http://e2etests", identifier));
            using FhirResponse<Observation> response = await _client.CreateAsync(observation);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var observation2 = Samples.GetDefaultObservation().ToPoco<Observation>();

            using FhirResponse<Observation> updateResponse = await _client.CreateAsync(
                observation2,
                $"identifier={identifier}");

            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
            Assert.Null(updateResponse.Resource);
        }

        [Fact]
        [Trait(Traits.Priority, Priority.One)]
        public async Task GivenAResource_WhenCreatingConditionallyWithMultipleMatches_TheServerShouldFail()
        {
            var observation = Samples.GetDefaultObservation().ToPoco<Observation>();
            var identifier = Guid.NewGuid().ToString();

            observation.Identifier.Add(new Identifier("http://e2etests", identifier));

            using FhirResponse<Observation> response = await _client.CreateAsync(observation);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            using FhirResponse<Observation> response2 = await _client.CreateAsync(observation);
            Assert.Equal(HttpStatusCode.Created, response2.StatusCode);

            var observation2 = Samples.GetDefaultObservation().ToPoco<Observation>();
            observation2.Id = Guid.NewGuid().ToString();

            var exception = await Assert.ThrowsAsync<FhirException>(() => _client.CreateAsync(
                observation2,
                $"identifier={identifier}"));

            Assert.Equal(HttpStatusCode.PreconditionFailed, exception.Response.StatusCode);
        }

        [Fact]
        [Trait(Traits.Priority, Priority.One)]
        public async Task GivenAResource_WhenCreatingConditionallyWithEmptyIfNoneHeader_TheServerShouldFail()
        {
            var exception = await Assert.ThrowsAsync<FhirException>(() => _client.CreateAsync(
                Samples.GetDefaultObservation().ToPoco<Observation>(),
                "&"));

            Assert.Equal(HttpStatusCode.BadRequest, exception.Response.StatusCode);
        }

        [Fact]
        [Trait(Traits.Priority, Priority.One)]
        public async Task GivenConcurrentResources_WhenCreatingConditionallyWithNoIdAndNoExisting_TheServerShouldReturnOneResourceSuccessfully()
        {
            string identifier = Guid.NewGuid().ToString();
            string value = Guid.NewGuid().ToString();

            int count = 0;

            var observation = Samples.GetDefaultObservation().ToPoco<Observation>();
            observation.Id = null;
            observation.Identifier.Add(new Identifier(null, identifier));
            observation.Value = new FhirString(value);

            async Task<HttpStatusCode> ExecuteRequest()
            {
                try
                {
                    // Both criteria should link to the same resource, but with different queries
                    string criteria = $"identifier={identifier}";

                    if (Interlocked.Increment(ref count) % 2 == 0)
                    {
                        criteria = $"value-string={value}";
                    }

                    var response = await _client.CreateAsync(observation, criteria);
                    return response.StatusCode;
                }
                catch (FhirException ex)
                {
                    return ex.StatusCode;
                }
            }

            HttpStatusCode[] tasks = await Task.WhenAll(Enumerable.Range(1, 30)
                .Select(_ => ExecuteRequest()));

            foreach (var item in tasks)
            {
                _logger.WriteLine("Status:" + item);
            }

            Assert.Equal(1, tasks.Count(x => x == HttpStatusCode.Created));
            Assert.Equal(tasks.Length - 1, tasks.Count(x => x == HttpStatusCode.Conflict || x == HttpStatusCode.OK || x == HttpStatusCode.PreconditionFailed));
        }
    }
}
