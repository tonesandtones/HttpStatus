const each = require('jest-each').default;
const sum = (a, b) => a + b;

let testCases = [
    [1, 1, 2],
    [2, 3, 5]
];

describe('Integration', () => {
    each(testCases)
        .test('%p + %p expects %p', (a, b, expected) => {
            expect(sum(a, b)).toBe(expected);
        })
})
