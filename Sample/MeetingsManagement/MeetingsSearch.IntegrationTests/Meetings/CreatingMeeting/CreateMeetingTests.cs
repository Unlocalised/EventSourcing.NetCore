using Core.Events;
using Core.Testing;
using FluentAssertions;
using MeetingsSearch.Meetings;
using MeetingsSearch.Meetings.CreatingMeeting;
using Xunit;
using Ogooreck.API;
using static Ogooreck.API.ApiSpecification;

namespace MeetingsSearch.IntegrationTests.Meetings.CreatingMeeting;

public class CreateMeetingTests(TestWebApplicationFactory<Program> fixture)
    : IClassFixture<TestWebApplicationFactory<Program>>
{
    private readonly ApiSpecification<Program> API = ApiSpecification<Program>.Setup(fixture);

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task CreateCommand_ShouldPublish_MeetingCreateEvent()
    {
        await fixture.PublishInternalEvent(new EventEnvelope<MeetingCreated>(
            new MeetingCreated(
                MeetingId,
                MeetingName
            ),
            new EventMetadata("event-id", 1, 2, null)
        ));

        await API.Given()
            .When(
                GET,
                URI($"{MeetingsSearchApi.MeetingsUrl}?filter={MeetingName}")
            )
            .Until(
                RESPONSE_BODY_MATCHES<IReadOnlyCollection<Meeting>>(
                    meetings => meetings.Any(m => m.Id == MeetingId))
            )
            .Then(
                RESPONSE_BODY<IReadOnlyCollection<Meeting>>(meetings =>
                    meetings.Should().Contain(meeting =>
                        meeting.Id == MeetingId
                        && meeting.Name == MeetingName
                    )
                ));
    }

    private readonly Guid MeetingId = Guid.NewGuid();
    private readonly string MeetingName = "Event Sourcing Workshop";
}
