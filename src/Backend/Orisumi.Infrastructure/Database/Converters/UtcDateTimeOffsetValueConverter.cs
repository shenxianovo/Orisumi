using Orisumi.Core;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Orisumi.Infrastructure.Database.Converters;

public sealed class UtcDateTimeOffsetValueConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => ObservationTime.Normalize(value), value => ObservationTime.Normalize(value));
