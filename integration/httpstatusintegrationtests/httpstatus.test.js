const axios = require('axios').default;

describe('hello world', () => {
    test('GET /', async () => {
        let result = null;
        await axios.get("http://localhost:8080", {timeout: 1000})
            .then(resp => {
                result = resp;
            })
        expect(result.status).toBe(200);
    })
})
