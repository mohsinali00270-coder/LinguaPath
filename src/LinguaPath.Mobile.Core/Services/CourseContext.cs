namespace LinguaPath.Mobile.Core.Services;

/// <summary>Which of the learner's courses Home and Learn show (FR-L24 course switcher). Remembered across launches.</summary>
public interface ICourseContext
{
    Task<int?> GetAsync();
    Task SetAsync(int learnerCourseId);
    void Clear();
}

public sealed class CourseContext : ICourseContext
{
    private const string Key = "lp.course";
    private readonly ISecureStore _store;
    private int? _current;

    public CourseContext(ISecureStore store, IAuthSession session)
    {
        _store = store;
        session.SignedOut += (_, _) => Clear();
    }

    public async Task<int?> GetAsync()
    {
        if (_current is not null) return _current;
        _current = int.TryParse(await _store.GetAsync(Key), out var id) ? id : null;
        return _current;
    }

    public async Task SetAsync(int learnerCourseId)
    {
        _current = learnerCourseId;
        await _store.SetAsync(Key, learnerCourseId.ToString());
    }

    public void Clear()
    {
        _current = null;
        _store.Remove(Key);
    }
}
