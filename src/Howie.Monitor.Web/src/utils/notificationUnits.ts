export function ruleUnit(unit: string) { return unit === 'B' ? 'GiB' : unit === 'B/s' ? 'MiB/s' : unit }
export function ruleScale(unit: string) { return unit === 'B' ? 1024 ** 3 : unit === 'B/s' ? 1024 ** 2 : 1 }
export function ruleInput(value: number, unit: string) { return Number((value / ruleScale(unit)).toPrecision(12)) }
export function ruleValue(value: string, unit: string) { return value.trim() === '' ? Number.NaN : Number(value) * ruleScale(unit) }
