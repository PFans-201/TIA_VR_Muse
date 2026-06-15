/// Common control surface for driving the dual (rest + active-VR) baseline,
/// implemented by both MuseUdpAdapter (forwards UDP commands to the Python bridge)
/// and MuseDirectAdapter (calls the in-process signal processor). TutorialManager
/// talks to this interface so it doesn't care which EEG path is in use.
public interface IMuseBaselineControl
{
    void StartRestBaseline();
    void StopRestBaseline();
    void StartActiveBaseline();
    void FinalizeBaseline();
    void ResetBaseline();
}
