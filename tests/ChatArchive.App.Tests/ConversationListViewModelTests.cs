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
}
