using ChatArchive.App.ViewModels;
using ChatArchive.Core.Models;
using Xunit;

namespace ChatArchive.App.Tests;

public class ConversationListViewModelTests
{
    [Fact]
    public void Activate_FiresEvent_EvenWhenSameConversation()
    {
        var vm = new ConversationListViewModel(null!, null!);
        var conversation = new ConversationInfo(
            Id: 1,
            Platform: "qq",
            AccountId: "acc",
            NativeId: "123",
            Kind: "private",
            Title: "Test Chat",
            FirstMessageAt: 1700000000000,
            LastMessageAt: 1700000000000,
            MessageCount: 10,
            LastMessagePreview: "Hello",
            MissingMediaCount: 0);

        var activations = 0;
        vm.ConversationActivated += info =>
        {
            if (info.Id == conversation.Id)
            {
                activations++;
            }
        };

        // First activation
        vm.Activate(conversation);
        Assert.Equal(1, activations);
        Assert.Equal(conversation, vm.SelectedConversation);

        // Second activation with identical conversation instance / equal record
        vm.Activate(conversation);
        Assert.Equal(2, activations);

        // Third activation with cloned record with same values
        var cloned = conversation with { };
        vm.Activate(cloned);
        Assert.Equal(3, activations);
    }

    [Fact]
    public void ApplyReload_ReselectsPreviousId_FromNewInstances()
    {
        var vm = new ConversationListViewModel(null!, null!);
        var selected = Conversation(8, "old preview");
        vm.Activate(selected);

        var reloaded = Conversation(8, "imported preview");
        vm.ApplyReload([reloaded, Conversation(9, "other")], refreshSelectedTimeline: false);

        Assert.NotNull(vm.SelectedConversation);
        Assert.Equal(8, vm.SelectedConversation.Id);
        Assert.Equal("imported preview", vm.SelectedConversation.LastMessagePreview);
        Assert.Same(reloaded, vm.SelectedConversation);
    }

    [Fact]
    public void ApplyReload_RefreshTimeline_ActivatesEvenWhenRecordEquals()
    {
        var vm = new ConversationListViewModel(null!, null!);
        var conversation = Conversation(1);
        var activations = 0;
        vm.ConversationActivated += info =>
        {
            if (info.Id == conversation.Id)
            {
                activations++;
            }
        };

        vm.Activate(conversation);
        Assert.Equal(1, activations);

        var equalRecord = conversation with { };
        vm.ApplyReload([equalRecord], refreshSelectedTimeline: true);

        Assert.Equal(2, activations);
        Assert.Equal(equalRecord.Id, vm.SelectedConversation?.Id);
    }

    [Fact]
    public void ApplyReload_WithoutRefresh_DoesNotActivateWhenRecordEquals()
    {
        var vm = new ConversationListViewModel(null!, null!);
        var conversation = Conversation(1);
        var activations = 0;
        vm.ConversationActivated += info =>
        {
            if (info.Id == conversation.Id)
            {
                activations++;
            }
        };

        vm.Activate(conversation);
        Assert.Equal(1, activations);

        var equalRecord = conversation with { };
        vm.ApplyReload([equalRecord], refreshSelectedTimeline: false);

        Assert.Equal(1, activations);
        Assert.Equal(conversation, vm.SelectedConversation);
    }

    private static ConversationInfo Conversation(long id, string? preview = "Hello")
    {
        return new ConversationInfo(
            Id: id,
            Platform: "qq",
            AccountId: "acc",
            NativeId: id.ToString(),
            Kind: "private",
            Title: "Test Chat",
            FirstMessageAt: 1700000000000,
            LastMessageAt: 1700000000000,
            MessageCount: 10,
            LastMessagePreview: preview,
            MissingMediaCount: 0);
    }
}
