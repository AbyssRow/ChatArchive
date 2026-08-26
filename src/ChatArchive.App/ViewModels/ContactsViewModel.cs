using System.Collections.ObjectModel;
using ChatArchive.Core.IO;
using ChatArchive.Core.Models;
using ChatArchive.Core.Repositories;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatArchive.App.ViewModels;

public partial class ContactsViewModel : ObservableObject
{
    private readonly ContactRepository _contactRepository;
    private readonly AvatarStorageService? _avatarStorageService;

    public ObservableCollection<ContactInfo> Contacts { get; } = new();

    [ObservableProperty]
    public partial ContactInfo? SelectedContact { get; set; }

    [ObservableProperty]
    public partial ContactDetailViewModel? SelectedDetail { get; set; }

    [ObservableProperty]
    public partial string SearchKeyword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public ContactsViewModel(
        ContactRepository contactRepository,
        AvatarStorageService? avatarStorageService = null)
    {
        _contactRepository = contactRepository;
        _avatarStorageService = avatarStorageService;
    }

    public async Task LoadAsync(string? keyword = null)
    {
        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            var query = keyword ?? SearchKeyword;
            var list = await Task.Run(() => _contactRepository.ListContacts(
                string.IsNullOrWhiteSpace(query) ? null : query.Trim()));

            Contacts.Clear();
            foreach (var item in list)
            {
                Contacts.Add(item);
            }

            if (SelectedContact is not null)
            {
                var match = Contacts.FirstOrDefault(c => c.Id == SelectedContact.Id);
                SelectedContact = match;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载联系人列表失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SelectContactAsync(ContactInfo? contact)
    {
        SelectedContact = contact;

        if (contact is null)
        {
            SelectedDetail = null;
            return;
        }

        var detailVm = new ContactDetailViewModel(_contactRepository, _avatarStorageService);
        var loaded = await detailVm.LoadAsync(contact.Id);
        if (loaded)
        {
            SelectedDetail = detailVm;
        }
        else
        {
            SelectedDetail = null;
        }
    }

    public async Task<ContactDetailViewModel> CreateNewContactAsync(
        string displayName,
        string? note = null,
        string? customAvatarPath = null,
        IEnumerable<(long SenderId, string? Label, bool IsPrimary)>? initialBindings = null)
    {
        var newId = await Task.Run(() => _contactRepository.CreateContact(
            displayName,
            customAvatarPath,
            note,
            initialBindings));

        await LoadAsync();

        var createdContact = Contacts.FirstOrDefault(c => c.Id == newId);
        if (createdContact is not null)
        {
            await SelectContactAsync(createdContact);
        }
        else
        {
            var detailVm = new ContactDetailViewModel(_contactRepository, _avatarStorageService);
            await detailVm.LoadAsync(newId);
            SelectedDetail = detailVm;
        }

        return SelectedDetail!;
    }

    public async Task DeleteContactAsync(long contactId)
    {
        await Task.Run(() => _contactRepository.DeleteContact(contactId));

        if (SelectedContact?.Id == contactId)
        {
            SelectedContact = null;
            SelectedDetail = null;
        }

        await LoadAsync();
    }
}
