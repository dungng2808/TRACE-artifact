using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Core.Engine;

public sealed record ExperimentScheduleItem(
    int ScheduleIndex,
    ExperimentTask Task,
    RepairStrategy Strategy,
    int Repetition,
    string BlockId,
    int TreatmentPosition);

public static class ExperimentScheduleBuilder
{
    public static IReadOnlyList<ExperimentScheduleItem> Build(
        IReadOnlyList<ExperimentTask> tasks,
        ExperimentConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();

        if (tasks.Count == 0)
        {
            throw new ArgumentException("At least one experiment task is required.", nameof(tasks));
        }

        var baseOrder = configuration.Strategies.ToArray();
        ShuffleDeterministically(baseOrder, configuration.ScheduleSeed);
        var schedule = new List<ExperimentScheduleItem>(
            tasks.Count * configuration.Repetitions * baseOrder.Length);

        for (var taskIndex = 0; taskIndex < tasks.Count; taskIndex++)
        {
            var task = tasks[taskIndex];
            for (var repetition = 1; repetition <= configuration.Repetitions; repetition++)
            {
                var rotation = (taskIndex + repetition - 1) % baseOrder.Length;
                var blockId = $"{task.TaskId}::R{repetition:D3}";
                for (var position = 0; position < baseOrder.Length; position++)
                {
                    schedule.Add(new ExperimentScheduleItem(
                        schedule.Count + 1,
                        task,
                        baseOrder[(position + rotation) % baseOrder.Length],
                        repetition,
                        blockId,
                        position + 1));
                }
            }
        }

        return schedule;
    }

    private static void ShuffleDeterministically(RepairStrategy[] values, int seed)
    {
        var state = unchecked((uint)seed);
        for (var index = values.Length - 1; index > 0; index--)
        {
            state = unchecked(state * 1_664_525u + 1_013_904_223u);
            var swapIndex = (int)(state % (uint)(index + 1));
            (values[index], values[swapIndex]) = (values[swapIndex], values[index]);
        }
    }
}
