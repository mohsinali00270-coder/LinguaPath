using LinguaPath.Application.Features.Practice;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;

namespace LinguaPath.Infrastructure.Persistence.Seed;

/// <summary>A small published practice library for the sample Spanish course (Phase 6), so every exercise type can be tried.</summary>
internal static class SamplePractice
{
    public static List<PracticeItem> ForSpanish(int languageId)
    {
        PracticeItem Item(PracticeType type, CefrLevel level, string title, string topic, string text, string? translation,
            params ReadingQuestions.Stored[] questions) => new()
        {
            LanguageId = languageId, Type = type, Level = level, Title = title, Topic = topic, Text = text, Translation = translation,
            QuestionsJson = questions.Length == 0 ? null : ReadingQuestions.Serialize(questions),
            Status = ContentStatus.Published, Source = ContentSource.Manual
        };

        ReadingQuestions.Stored Q(string question, int correct, string explanation, params string[] options) =>
            new() { Question = question, Options = options.ToList(), Correct = correct, Explanation = explanation };

        return
        [
            Item(PracticeType.Read, CefrLevel.A1, "Mi familia", "Family",
                "Me llamo Lucía y vivo en Sevilla. Tengo una familia pequeña. Mi madre se llama Carmen y es profesora. " +
                "Mi padre se llama Andrés y trabaja en un hospital. Tengo un hermano, Pablo. Él tiene diez años y le gusta el fútbol. " +
                "Los domingos comemos juntos en casa de mi abuela.",
                "My name is Lucía and I live in Seville. I have a small family. My mother is called Carmen and she is a teacher. " +
                "My father is called Andrés and he works in a hospital. I have a brother, Pablo. He is ten and he likes football. " +
                "On Sundays we eat together at my grandmother's house.",
                Q("¿Dónde vive Lucía?", 1, "\"Vivo en Sevilla.\"", "En Madrid", "En Sevilla", "En Valencia"),
                Q("¿Qué hace la madre de Lucía?", 0, "\"Es profesora.\"", "Es profesora", "Es médica", "Trabaja en una tienda"),
                Q("¿Qué le gusta a Pablo?", 2, "\"Le gusta el fútbol.\"", "La música", "Leer", "El fútbol")),

            Item(PracticeType.Read, CefrLevel.A2, "Un día en el mercado", "Shopping",
                "El sábado por la mañana fui al mercado con mi amigo Tomás. Compramos tomates, pan y un poco de queso. " +
                "La fruta estaba muy barata, así que también compré naranjas. Después tomamos un café en una terraza " +
                "y hablamos de nuestros planes para el verano. Tomás quiere viajar a México.",
                "On Saturday morning I went to the market with my friend Tomás. We bought tomatoes, bread and a bit of cheese. " +
                "The fruit was very cheap, so I also bought oranges. Then we had a coffee on a terrace and talked about our summer plans. " +
                "Tomás wants to travel to Mexico.",
                Q("¿Cuándo fueron al mercado?", 0, "\"El sábado por la mañana.\"", "El sábado por la mañana", "El domingo", "El viernes por la tarde"),
                Q("¿Por qué compró naranjas?", 1, "\"La fruta estaba muy barata.\"", "Porque Tomás las quería", "Porque la fruta estaba barata", "Para un pastel"),
                Q("¿Adónde quiere viajar Tomás?", 2, "\"Tomás quiere viajar a México.\"", "A Perú", "A Argentina", "A México")),

            Item(PracticeType.Write, CefrLevel.A1, "Preséntate", "Introduce yourself",
                "Escribe 30-50 palabras para presentarte: tu nombre, de dónde eres, dónde vives y qué te gusta hacer.",
                "Write 30-50 words to introduce yourself: your name, where you are from, where you live and what you like doing."),
            Item(PracticeType.Write, CefrLevel.A2, "Mi fin de semana", "Weekend plans",
                "Escribe un mensaje de 50-80 palabras a un amigo. Cuéntale qué hiciste el fin de semana pasado y qué vas a hacer el próximo.",
                "Write a 50-80 word message to a friend. Tell them what you did last weekend and what you are going to do next weekend."),

            Item(PracticeType.Speak, CefrLevel.A1, "Saludo", "Greetings", "Buenos días, ¿cómo estás?", "Good morning, how are you?"),
            Item(PracticeType.Speak, CefrLevel.A1, "Presentación", "Introduce yourself", "Me llamo Ana y soy de Madrid.", "My name is Ana and I'm from Madrid."),
            Item(PracticeType.Speak, CefrLevel.A2, "En el restaurante", "Food and cooking", "Quisiera una mesa para dos personas, por favor.",
                "I would like a table for two, please."),

            Item(PracticeType.Listen, CefrLevel.A1, "Dictado 1", "Daily routine", "Me levanto a las siete de la mañana.", "I get up at seven in the morning."),
            Item(PracticeType.Listen, CefrLevel.A1, "Dictado 2", "Family", "Mi hermana tiene dos gatos negros.", "My sister has two black cats."),
            Item(PracticeType.Listen, CefrLevel.A2, "Dictado 3", "Travel", "El tren sale del andén número cuatro.", "The train leaves from platform four.")
        ];
    }
}
